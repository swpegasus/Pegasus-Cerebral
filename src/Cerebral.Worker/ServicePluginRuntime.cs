using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Collections.ObjectModel;
using Cerebral.Contracts.Routing;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Workflows;
using Cerebral.Contracts.Agents;
using Cerebral.Core;
using Cerebral.Engine.Workflows;
using Cerebral.Engine.Routing;
using Cerebral.Tools;

namespace Cerebral.Worker;

public sealed record ServicePluginStatus(string Id, string Name, string Version, bool Loaded,
    IReadOnlyList<string> Tools, IReadOnlyList<string> Commands, string? Error = null);

/// <summary>Host-frozen facts from one successfully published service package instance.</summary>
internal sealed record LoadedWorkflowPortableSettings(
    string PackageId, string PackageVersion, string LoadedGeneration,
    string PackageFingerprint, string SchemaFingerprint,
    ServicePluginPortableSettingsBinding Schema,
    IServiceWorkflowPortableSettingsValidator? Validator, string PackageDirectory);

/// <summary>Loads cross-platform service contributions without giving the Worker knowledge of their domains.</summary>
public sealed partial class ServicePluginRuntime : IDisposable
{
    public const string ManifestFileName = "service-plugin.json";
    private static readonly JsonSerializerOptions ContextDescriptorJson = new(CerebralJson.Options) { WriteIndented = false };
    private readonly List<LoadedPlugin> loaded = [];
    private readonly List<ServicePluginStatus> status = [];
    private readonly Dictionary<string, ServicePluginPackageBinding> bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ServicePluginPortableSettingsBinding> portableSettingsSchemas = new(StringComparer.Ordinal);
    private Dictionary<string, IServiceCommandExtension> commands = new(StringComparer.Ordinal);
    private readonly object contextContributionGate = new();
    private readonly List<ServiceWorkflowContextRegistration> contextContributions = [];
    internal IReadOnlyList<ServiceWorkflowContextRegistration> WorkflowContextContributions
    {
        get
        {
            lock (contextContributionGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return Array.AsReadOnly(contextContributions.ToArray());
            }
        }
    }
    private bool disposed;
    private Task committedRootUserInputSettlement = Task.CompletedTask;
    internal ICommittedRootUserInputDeliveryGate CommittedRootUserInputGate => new CommittedRootUserInputRuntimeGate(this);
    internal bool HasCommittedRootUserInputObservers
    {
        get
        {
            lock (contextContributionGate) return !disposed && loaded.Any(item =>
                item.Plugin is IServiceCommittedRootUserInputObserver &&
                bindings.TryGetValue(item.Host.PluginId, out var current) && IsCurrentLoadedPlugin(item, current));
        }
    }
    internal Task CommittedRootUserInputSettlement
    {
        get { lock (contextContributionGate) return committedRootUserInputSettlement; }
    }
    private readonly Func<string, string, IPluginSettingsPolicy?, IScopedPluginSettings>? settingsFactory;
    private readonly Dictionary<string, SettingsPolicyBinding> settingsPolicies = new(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, SettingsPolicyBinding> SettingsPolicies =>
        new ReadOnlyDictionary<string, SettingsPolicyBinding>(settingsPolicies);

    public IReadOnlyList<ServicePluginStatus> Status => status.ToList();
    public IReadOnlyList<ServicePluginPackageBinding> PackageBindings => Array.AsReadOnly(bindings.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray());
    public IReadOnlyDictionary<string, ServicePluginPortableSettingsBinding> PortableSettingsSchemas =>
        new ReadOnlyDictionary<string, ServicePluginPortableSettingsBinding>(
            portableSettingsSchemas.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
    public IReadOnlyList<IToolExtension> ToolExtensions => loaded.SelectMany(x => x.Tools).ToArray();
    public IReadOnlyList<IWorkflowModule> WorkflowModules => loaded.Where(x => x.WorkflowModule is not null)
        .Select(x => x.WorkflowModule!).ToArray();
    public IReadOnlyList<IResponseOutputHook> ResponseHooks => loaded.SelectMany(x => x.ResponseHooks).ToArray();
    public IReadOnlyList<ICommittedOutputObserver> OutputObservers => loaded.SelectMany(x => x.OutputObservers).ToArray();
    public IReadOnlyList<IWorkflowAuditAdapter> WorkflowAuditors => loaded.SelectMany(x => x.WorkflowAuditors).ToArray();

    // Resolve only published tool registrations. Pre-load manifest rows are never workflow owners.
    internal string? LoadedWorkflowToolOwner(string toolName) => loaded
        .SingleOrDefault(item => item.ToolNames.Contains(toolName, StringComparer.Ordinal))?.Host.PluginId;

    internal LoadedWorkflowPortableSettings? WorkflowPortableSettingsForTool(string toolName) =>
        LoadedWorkflowToolOwner(toolName) is { } owner ? LoadedWorkflowPortableSettingsForPackage(owner) : null;

    internal LoadedWorkflowPortableSettings? LoadedWorkflowPortableSettingsForPackage(string id) => loaded
        .SingleOrDefault(item => item.Host.PluginId == id)?.WorkflowPortableSettings;

    public ServicePluginRuntime(string programPluginDirectory, string dataDirectory, EventHub events)
        : this(programPluginDirectory, dataDirectory, events, null) { }

    internal ServicePluginRuntime(string programPluginDirectory, string dataDirectory, EventHub events,
        Func<string, string, IPluginSettingsPolicy?, IScopedPluginSettings>? settingsFactory)
    {
        this.settingsFactory = settingsFactory;
        var root = Path.GetFullPath(programPluginDirectory);
        var dataRoot = Path.GetFullPath(dataDirectory);
        if (!Directory.Exists(root)) return;
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        // Resolve every manifest before executing any third-party constructor or initialization code.
        foreach (var selection in ServicePackageLifecycle.ResolveRepository(root))
        {
            if (selection.Error is not null)
            {
                status.Add(new(selection.Id, selection.Id, "", false, [], [], selection.Error));
                bindings.Add(selection.Id, new(selection.Id, "", false,
                    Array.AsReadOnly(Array.Empty<ServicePluginDependencyBinding>()), selection.Error, selection.Generation));
                continue;
            }
            var package = selection.Directory;
            var manifestPath = Path.Combine(package, ManifestFileName);
            try
            {
                var manifest = InspectPackage(package, manifestPath, selection.Id,
                    selection.Generation == "legacy");
                if (manifest.Id != selection.Id)
                    throw new InvalidDataException("The selected service package generation does not match its lifecycle identity.");
                candidates.Add(manifest.Id, new(package, manifest, selection.Generation));
                if (manifest.PortableSettings is { } portable)
                    portableSettingsSchemas.Add(manifest.Id,
                        ServicePluginPortableSettingsPolicy.Freeze(manifest.Id, manifest.Version, portable));
            }
            catch (Exception ex)
            {
                var id = selection.Id;
                status.Add(new(id, id, "", false, [], [], Error(ex)));
                bindings.Add(id, new(id, "", false, Array.AsReadOnly(Array.Empty<ServicePluginDependencyBinding>()), Error(ex), selection.Generation));
            }
        }
        MarkCycles(candidates);
        foreach (var candidate in candidates.Values.OrderBy(x => x.Manifest.Id, StringComparer.Ordinal))
            LoadResolved(candidate, candidates, dataRoot, events);
    }

    public IServiceCommandExtension? CommandFor(string operation) => commands.GetValueOrDefault(operation);

    private void LoadResolved(Candidate candidate, IReadOnlyDictionary<string, Candidate> candidates, string dataRoot, EventHub events)
    {
        var manifest = candidate.Manifest;
        if (bindings.ContainsKey(manifest.Id)) return;
        if (candidate.Error is not null)
        {
            Reject(candidate, ResolveDependencies(manifest, candidates), candidate.Error);
            return;
        }
        foreach (var dependency in manifest.Dependencies.OrderBy(x => x.Id, StringComparer.Ordinal))
            if (candidates.TryGetValue(dependency.Id, out var target) && DependencyCompatible(dependency, target.Manifest.Version))
                LoadResolved(target, candidates, dataRoot, events);
        var resolved = ResolveDependencies(manifest, candidates);
        var unavailable = resolved.FirstOrDefault(x => !x.Optional && !x.Available);
        if (unavailable is not null)
        {
            Reject(candidate, resolved, "Required dependency '" + unavailable.Id + "' is unavailable: " + unavailable.Reason);
            return;
        }
        Load(candidate.Package, candidate.Generation, manifest, resolved, dataRoot, events);
    }

    private IReadOnlyList<ServicePluginDependencyBinding> ResolveDependencies(ServicePluginManifest manifest,
        IReadOnlyDictionary<string, Candidate> candidates) =>
        Array.AsReadOnly(manifest.Dependencies.OrderBy(x => x.Id, StringComparer.Ordinal).Select(dependency =>
        {
            var target = bindings.GetValueOrDefault(dependency.Id);
            candidates.TryGetValue(dependency.Id, out var candidate);
            var version = target?.Version ?? candidate?.Manifest.Version;
            var available = target is { Loaded: true } && DependencyCompatible(dependency, target.Version);
            var reason = available ? null : version is not null && !DependencyCompatible(dependency, version) ? "Installed version does not satisfy the declared constraint."
                : target is null ? candidate?.Error ?? "Package is not installed or could not be resolved."
                : !target.Loaded ? "Package did not load: " + target.Error : "Installed version does not satisfy the declared constraint.";
            return new ServicePluginDependencyBinding(dependency.Id, dependency.ExactVersion, dependency.MinimumVersion,
                dependency.MaximumVersionExclusive, dependency.Optional, version, available, reason);
        }).ToArray());

    private void Reject(Candidate candidate, IReadOnlyList<ServicePluginDependencyBinding> dependencies, string error)
    {
        var manifest = candidate.Manifest;
        status.Add(new(manifest.Id, manifest.Name, manifest.Version, false, [], [], error));
        bindings.Add(manifest.Id, new(manifest.Id, manifest.Version, false, dependencies, error, candidate.Generation));
    }

    private static void MarkCycles(IReadOnlyDictionary<string, Candidate> candidates)
    {
        var manifests = candidates.ToDictionary(pair => pair.Key, pair => pair.Value.Manifest,
            StringComparer.Ordinal);
        foreach (var component in DependencyCycles(manifests))
        {
            var error = "Plugin dependency cycle among: " + string.Join(", ", component);
            foreach (var id in component) candidates[id].Error = error;
        }
    }

    // The loader and stopped-owner recovery use the same code-free compatible-edge cycle rule.
    internal static IReadOnlyList<IReadOnlyList<string>> DependencyCycles(
        IReadOnlyDictionary<string, ServicePluginManifest> manifests)
    {
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var lows = new Dictionary<string, int>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        var active = new HashSet<string>(StringComparer.Ordinal);
        var cycles = new List<IReadOnlyList<string>>();
        void Visit(string id)
        {
            var manifest = manifests[id];
            indices[id] = lows[id] = indices.Count;
            pending.Push(id); active.Add(id);
            foreach (var dependency in manifest.Dependencies.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                if (!manifests.TryGetValue(dependency.Id, out var target) ||
                    !DependencyCompatible(dependency, target.Version)) continue;
                if (!indices.ContainsKey(dependency.Id))
                {
                    Visit(dependency.Id);
                    lows[id] = Math.Min(lows[id], lows[dependency.Id]);
                }
                else if (active.Contains(dependency.Id)) lows[id] = Math.Min(lows[id], indices[dependency.Id]);
            }
            if (lows[id] != indices[id]) return;
            var component = new List<string>();
            string member;
            do { member = pending.Pop(); active.Remove(member); component.Add(member); } while (member != id);
            if (component.Count == 1 && !manifest.Dependencies.Any(x => x.Id == id &&
                    DependencyCompatible(x, manifest.Version))) return;
            cycles.Add(component.Order(StringComparer.Ordinal).ToArray());
        }
        foreach (var id in manifests.Keys.Order(StringComparer.Ordinal))
            if (!indices.ContainsKey(id)) Visit(id);
        return cycles;
    }

    internal static bool DependencyCompatible(ServicePluginDependency dependency, string version)
    {
        if (!PluginVersion.TryParse(version, out var parsed)) return false;
        if (dependency.ExactVersion is not null) return string.Equals(dependency.ExactVersion, version, StringComparison.Ordinal);
        return (dependency.MinimumVersion is null || parsed!.CompareTo(PluginVersion.Parse(dependency.MinimumVersion)) >= 0) &&
               (dependency.MaximumVersionExclusive is null || parsed!.CompareTo(PluginVersion.Parse(dependency.MaximumVersionExclusive)) < 0);
    }

    private void Load(string package, string generation, ServicePluginManifest manifest,
        IReadOnlyList<ServicePluginDependencyBinding> dependencies, string dataRoot, EventHub events)
    {
        PluginLoadContext? context = null;
        IServicePlugin? plugin = null;
        Host? pluginHost = null;
        ServiceWorkflowContextRegistration[] candidateContext = [];
        try
        {
            var portableSchema = portableSettingsSchemas.GetValueOrDefault(manifest.Id);
            var packageFingerprint = portableSchema is null ? null : ServicePackageLifecycle.FingerprintPackage(package);
            context = new PluginLoadContext(ContainedPath(package, manifest.Assembly), package);
            var assembly = context.LoadFromAssemblyPath(ContainedPath(package, manifest.Assembly));
            var type = assembly.GetType(manifest.EntryType, true)!;
            if (!type.IsPublic || type.IsAbstract || !typeof(IServicePlugin).IsAssignableFrom(type))
                throw new InvalidDataException("The service entry type must be a public, concrete IServicePlugin implementation.");
            plugin = (IServicePlugin)(Activator.CreateInstance(type) ?? throw new InvalidDataException("The service plugin could not be constructed."));
            var workflowSettingsValidator = plugin as IServiceWorkflowPortableSettingsValidator;
            if (workflowSettingsValidator is not null)
            {
                int validatorVersion;
                try { validatorVersion = workflowSettingsValidator.WorkflowPortableSettingsValidatorVersion; }
                catch (Exception)
                {
                    // Plugin exceptions may contain settings or other private values. Public status
                    // receives only the host-authored refusal, even before the plugin is published.
                    throw new InvalidDataException("The service package workflow settings validator could not be inspected.");
                }
                if (validatorVersion != ServiceWorkflowPortableSettingsValidatorContract.Version)
                    throw new InvalidDataException("The service package workflow settings validator version is unsupported.");
            }
            var pluginData = Path.Combine(dataRoot, manifest.Id);
            Directory.CreateDirectory(pluginData);
            pluginHost = new Host(manifest.Id, package, pluginData, dependencies, events,
                settingsFactory?.Invoke(manifest.Id, manifest.Version, plugin as IPluginSettingsPolicy),
                QueueOwnSettingsRefresh);
            plugin.Initialize(pluginHost);
            var extensions = plugin.ToolExtensions.Select(extension => (IToolExtension)new ToolExtensionSnapshot(extension,
                Array.AsReadOnly(extension.Tools.Select(FreezeTool).ToArray()))).ToArray();
            var tools = extensions.SelectMany(x => x.Tools).ToList();
            var commandEntries = plugin.CommandExtensions
                .SelectMany(extension => extension.Operations.Select(operation => (operation, extension))).ToArray();
            var operations = commandEntries.Select(x => x.operation).ToList();
            ValidateContributions(tools, operations);
            var existingNames = loaded.SelectMany(x => x.ToolNames).Concat(commands.Keys).ToHashSet(StringComparer.Ordinal);
            foreach (var name in tools.Select(x => x.Name).Concat(operations))
                if (existingNames.Contains(name)) throw new InvalidDataException("Duplicate service contribution: " + name);

            IWorkflowModule? workflowModule = null;
            if (plugin is IWorkflowModule || plugin is IAgentHarnessModule)
            {
                var contribution = plugin as IWorkflowModule;
                if (contribution is not null && contribution.Id != manifest.Id)
                    throw new InvalidDataException("Workflow module identity must match its service package.");
                var harnesses = (plugin as IAgentHarnessModule)?.Harnesses ?? [];
                if (harnesses.Count > 128) throw new InvalidDataException("A service package can export at most 128 harnesses.");
                var candidateModule = new WorkflowModuleSnapshot(manifest.Id,
                    (contribution?.Workflows ?? []).Concat(harnesses.Select(harness => (IWorkflowDefinition)new AgentWorkflowDefinition(harness))).ToArray(),
                    (contribution?.Commands ?? []).ToArray());
                // Freeze the whole candidate before publication. The final Worker registry, built
                // after real host effects are appended, performs exact consumer-command admission.
                workflowModule = WorkflowRegistry.FreezeCandidateShape(WorkflowModules, candidateModule);
            }
            IResponseOutputHook[] responseHooks = [];
            ICommittedOutputObserver[] outputObservers = [];
            if (plugin is IOutputHookModule outputModule)
            {
                var responses = outputModule.ResponseHooks ?? throw new InvalidDataException("Output hook module has no response-hook list.");
                var observers = outputModule.OutputObservers ?? throw new InvalidDataException("Output hook module has no observer list.");
                if (responses.Count + observers.Count > 128)
                    throw new InvalidDataException("A service package can export at most 128 response/output hooks.");
                responseHooks = responses.Select(hook => Snapshot(manifest.Id, hook)).ToArray();
                outputObservers = observers.Select(observer => Snapshot(manifest.Id, observer)).ToArray();
                // Validate the complete candidate registry before publishing any hook from this package.
                _ = new ResponseOutputPipeline(ResponseHooks.Concat(responseHooks), OutputObservers.Concat(outputObservers));
            }
            var workflowAuditors = plugin is IWorkflowAuditModule auditModule
                ? (auditModule.WorkflowAuditors ?? throw new InvalidDataException("Audit module has no adapter list.")).ToArray() : [];
            if (workflowAuditors.Length > 8 || workflowAuditors.Any(adapter => adapter.Descriptor.PackageId != manifest.Id))
                throw new InvalidDataException("Audit adapters must use their exact admitted package identity; at most eight per package.");
            _ = new WorkflowAuditPipeline(WorkflowAuditors.Concat(workflowAuditors));
            var instanceToken = new object();
            var packageBinding = new ServicePluginPackageBinding(manifest.Id, manifest.Version, true,
                Array.AsReadOnly(dependencies.Select(value => value with { }).ToArray()), null, generation);
            pluginHost.BindSourceBoundPackage(packageBinding);
            if (plugin is IServiceWorkflowContextModule contextModule)
            {
                var declared = contextModule.ContextContributions
                    ?? throw new InvalidDataException("Context module has no contribution list.");
                var contributions = declared.Take(WorkflowContextContributionContract.MaximumContributions + 1).ToArray();
                if (declared.Count != contributions.Length || contributions.Length > WorkflowContextContributionContract.MaximumContributions ||
                    contextContributions.Count + contributions.Length > WorkflowContextContributionContract.MaximumContributions)
                    throw new InvalidDataException("Context module exceeds its contribution bound.");
                var identifiers = new HashSet<string>(contextContributions.Select(value => value.Contribution.Descriptor.Id), StringComparer.Ordinal);
                var providerBindings = contextContributions.GroupBy(value => value.Contribution.Descriptor.Provider.Id, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First().Contribution.Descriptor.Provider, StringComparer.Ordinal);
                var snapshots = new List<ServiceWorkflowContextRegistration>();
                var descriptorBytes = contextContributions.Sum(value => ContextDescriptorBytes(value.Contribution.Descriptor,
                    (value.Contribution as IWorkflowToolContextProjectionContribution)?.ProjectionDescriptor));
                try
                {
                    foreach (var contribution in contributions)
                    {
                        if (contribution is null) throw new InvalidDataException("Context contribution is absent.");
                        var descriptor = FreezeContextDescriptor(packageBinding, contribution.Descriptor);
                        var provider = workflowModule?.Commands.OfType<Cerebral.Contracts.Context.IContextProviderCommandHandler>()
                            .SingleOrDefault(value => value.Provider.Id == descriptor.Provider.Id && value.Provider.Version == descriptor.Provider.Version);
                        if (provider is null || !JsonElement.DeepEquals(CerebralJson.Element(provider.Provider), CerebralJson.Element(descriptor.Provider)))
                            throw new InvalidDataException("Context contribution must match its package's admitted provider.");
                        // The registry already validated and froze the provider's identity, kinds and bounds.
                        descriptor = descriptor with { Provider = provider.Provider };
                        var projectionDescriptor = WorkerToolContextProjectionAdmission.CaptureDescriptor(descriptor, contribution);
                        if (providerBindings.TryGetValue(provider.Provider.Id, out var priorProvider) &&
                            !JsonElement.DeepEquals(CerebralJson.Element(priorProvider), CerebralJson.Element(provider.Provider)))
                            throw new InvalidDataException("A context provider identity must use one frozen descriptor and version.");
                        providerBindings[provider.Provider.Id] = provider.Provider;
                        if (!identifiers.Add(descriptor.Id)) throw new InvalidDataException("Duplicate context contribution.");
                        descriptorBytes = checked(descriptorBytes + ContextDescriptorBytes(descriptor, projectionDescriptor));
                        if (descriptorBytes > WorkflowContextContributionContract.MaximumCollectionUtf8Bytes)
                            throw new InvalidDataException("Context descriptors exceed their collection bound.");
                        snapshots.Add(new(this, instanceToken, packageBinding, contribution, descriptor, projectionDescriptor));
                    }
                    candidateContext = snapshots.ToArray();
                }
                catch
                {
                    foreach (var snapshot in snapshots) snapshot.Retire();
                    throw;
                }
            }
            var candidateCommands = new Dictionary<string, IServiceCommandExtension>(commands, StringComparer.Ordinal);
            foreach (var entry in commandEntries)
                candidateCommands.Add(entry.operation, entry.extension);

            LoadedWorkflowPortableSettings? workflowPortableSettings = null;
            if (portableSchema is not null)
            {
                if (ServicePackageLifecycle.FingerprintPackage(package) != packageFingerprint)
                    throw new InvalidDataException("The service package changed while loading.");
                workflowPortableSettings = new(manifest.Id, manifest.Version, generation, packageFingerprint!,
                    WorkflowPortableSettingsApprovals.SchemaFingerprint(portableSchema), portableSchema,
                    workflowSettingsValidator, package);
            }

            loaded.Add(new(context, plugin, pluginHost, extensions, tools.Select(x => x.Name).ToArray(), workflowModule,
                responseHooks, outputObservers, workflowAuditors, instanceToken, workflowPortableSettings, packageBinding));
            commands = candidateCommands;
            if (plugin is IPluginSettingsPolicy settingsPolicy)
                settingsPolicies.Add(manifest.Id, new(manifest.Id, manifest.Version, settingsPolicy));
            status.Add(new(manifest.Id, manifest.Name, manifest.Version, true,
                tools.Select(x => x.Name).ToArray(), operations.ToArray()));
            bindings.Add(manifest.Id, packageBinding);
            lock (contextContributionGate) contextContributions.AddRange(candidateContext);
        }
        catch (Exception ex)
        {
            foreach (var contribution in candidateContext) contribution.Retire();
            pluginHost?.Dispose();
            try { plugin?.Dispose(); } catch { }
            context?.Unload();
            status.Add(new(manifest.Id, manifest.Name, manifest.Version, false, [], [], Error(ex)));
            bindings.Add(manifest.Id, new(manifest.Id, manifest.Version, false, dependencies, Error(ex), generation));
        }
    }

    internal static ServicePluginManifest InspectPackage(string package, string manifestPath,
        string? expectedId = null, bool requireMatchingDirectory = true)
    {
        var info = new FileInfo(manifestPath);
        if (info.Length is < 1 or > ServicePluginContract.MaxManifestBytes)
            throw new InvalidDataException("Service plugin manifest is empty or exceeds 64 KiB.");
        var manifestBytes = File.ReadAllBytes(manifestPath);
        if (manifestBytes.Length is < 1 or > ServicePluginContract.MaxManifestBytes)
            throw new InvalidDataException("Service plugin manifest changed size while it was being admitted.");
        using var document = JsonDocument.Parse(manifestBytes,
            new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow });
        ValidateManifestShape(document.RootElement);
        ServicePluginManifest manifest;
        try
        {
            manifest = document.RootElement.Deserialize<ServicePluginManifest>(new JsonSerializerOptions(CerebralJson.Options)
            {
                PropertyNameCaseInsensitive = false, MaxDepth = 16,
                RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
            }) ?? throw new InvalidDataException("Service plugin manifest is empty.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Service plugin manifest contains unsupported or invalid fields.");
        }
        if (manifest.SchemaVersion is not (1 or 2) || manifest.Contract != ServicePluginContract.Id || manifest.ApiVersion != ServicePluginContract.Version)
            throw new InvalidDataException("Service plugin contract or API version is unsupported.");
        ValidateRequiredHostFacets(manifest);
        ValidatePackageId(manifest.Id);
        if (manifest.Id == "cerebral.host") throw new InvalidDataException("The package ID is reserved by Cerebral's execution host.");
        if (expectedId is not null && manifest.Id != expectedId)
            throw new InvalidDataException("The selected service package does not match its lifecycle identity.");
        if (requireMatchingDirectory && !Path.GetFileName(package).Equals(manifest.Id, StringComparison.Ordinal))
            throw new InvalidDataException("Service plugin folder must match its manifest ID.");
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 100 || manifest.Name.Any(char.IsControl) ||
            !PluginVersion.TryParse(manifest.Version, out _) || string.IsNullOrWhiteSpace(manifest.EntryType) || manifest.EntryType.Length > 256)
            throw new InvalidDataException("Service plugin identity, version, or entry type is invalid.");
        if (manifest.Dependencies is null || manifest.Dependencies.Count > ServicePluginContract.MaxDependencies)
            throw new InvalidDataException("A service plugin may declare at most 32 dependencies.");
        if (manifest.PortableSettings is { } portableSettings)
            _ = ServicePluginPortableSettingsPolicy.Freeze(manifest.Id, manifest.Version, portableSettings);
        var dependencyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in manifest.Dependencies)
        {
            if (dependency is null) throw new InvalidDataException("A service plugin dependency cannot be null.");
            ValidatePackageId(dependency.Id);
            if (!dependencyIds.Add(dependency.Id)) throw new InvalidDataException("Duplicate plugin dependency: " + dependency.Id);
            var exact = dependency.ExactVersion is not null;
            var minimum = dependency.MinimumVersion is not null;
            var maximum = dependency.MaximumVersionExclusive is not null;
            if (exact == (minimum || maximum) || exact && !PluginVersion.TryParse(dependency.ExactVersion, out _) ||
                minimum && !PluginVersion.TryParse(dependency.MinimumVersion, out _) ||
                maximum && !PluginVersion.TryParse(dependency.MaximumVersionExclusive, out _) ||
                minimum && maximum && PluginVersion.Parse(dependency.MinimumVersion!).CompareTo(PluginVersion.Parse(dependency.MaximumVersionExclusive!)) >= 0)
                throw new InvalidDataException("Dependency '" + dependency.Id + "' requires an exact SemVer or a nonempty minimum-inclusive/maximum-exclusive range.");
        }
        var assembly = ContainedPath(package, manifest.Assembly);
        if (!assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(assembly))
            throw new InvalidDataException("The declared service plugin assembly is missing or is not a DLL.");
        return manifest;
    }

    private static void ValidateManifestShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Service plugin manifest must be a JSON object.");
        RejectDuplicateManifestFields(root);
        RequireManifestMembers(root, "schemaVersion", "contract", "apiVersion", "id", "name", "version",
            "description", "assembly", "entryType");
        if (!root.TryGetProperty("schemaVersion", out var schemaElement) || !schemaElement.TryGetInt32(out var schema))
            throw new InvalidDataException("Service plugin manifest schemaVersion is required and must be an integer.");
        if (schema == 1 && root.TryGetProperty("portableSettings", out _))
            throw new InvalidDataException("Service manifest schema 1 cannot declare portable settings, including null.");
        if (schema == 1 && root.TryGetProperty("requiredHostFacets", out _))
            throw new InvalidDataException("Service manifest schema 1 cannot declare required host facets.");
        if (root.TryGetProperty("requiredHostFacets", out var requiredHostFacets))
        {
            if (requiredHostFacets.ValueKind != JsonValueKind.Array ||
                requiredHostFacets.GetArrayLength() is < 1 or > ServicePluginContract.MaxRequiredHostFacets)
                throw new InvalidDataException("requiredHostFacets must contain between 1 and 8 facet names.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in requiredHostFacets.EnumerateArray())
            {
                var name = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (name is null || name.Length is < 1 or > 128 ||
                    name.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '/')))
                    throw new InvalidDataException("requiredHostFacets contains an invalid bounded facet name.");
                if (!names.Add(name))
                    throw new InvalidDataException("requiredHostFacets contains a duplicate facet name.");
            }
        }
        if (root.TryGetProperty("dependencies", out var dependencies))
        {
            if (dependencies.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Service plugin dependencies must be an array.");
            foreach (var dependency in dependencies.EnumerateArray())
            {
                if (dependency.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Service plugin dependencies must be objects.");
                RequireManifestMembers(dependency, "id", "optional");
            }
        }
        if (root.TryGetProperty("portableSettings", out var portable))
        {
            if (portable.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Service plugin portableSettings must be an object.");
            RequireManifestMembers(portable, "version", "fields");
            if (!portable.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Service plugin portableSettings fields must be an array.");
            foreach (var field in fields.EnumerateArray())
            {
                if (field.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Portable setting declarations must be objects.");
                RequireManifestMembers(field, "name", "type");
            }
        }
    }

    private static void RequireManifestMembers(JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (!value.TryGetProperty(name, out _))
                throw new InvalidDataException("Service plugin manifest omits a required contract field.");
    }

    private static void ValidateRequiredHostFacets(ServicePluginManifest manifest)
    {
        if (manifest.RequiredHostFacets is null) return;
        foreach (var facet in manifest.RequiredHostFacets)
            if (facet != ServicePluginContract.ScalarGenerationHostFacetV1 ||
                !typeof(IServiceScalarGenerationHostV1).IsAssignableFrom(typeof(Host)))
                throw new InvalidDataException("Required service host facet '" + facet + "' is unavailable on this Worker. Select a compatible Core/Worker cohort before staging this package.");
    }

    private static void RejectDuplicateManifestFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("Service plugin manifest contains duplicate fields.");
                RejectDuplicateManifestFields(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateManifestFields(item);
    }

    private static void ValidateContributions(IReadOnlyList<ToolDescriptor> tools, IReadOnlyList<string> operations)
    {
        if (tools.Count > 128 || operations.Count > 128) throw new InvalidDataException("A service plugin may contribute at most 128 tools and 128 owner commands.");
        foreach (var tool in tools)
            if (ToolDispatcher.IsHostToolName(tool.Name))
                throw new InvalidDataException("Service tool name is reserved by the execution host: " + tool.Name);
        foreach (var operation in operations)
            if (CommandDispatcher.IsHostOperationName(operation))
                throw new InvalidDataException("Service command namespace is reserved by the execution host: " + operation);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in tools.Select(x => x.Name).Concat(operations))
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
                throw new InvalidDataException("Service contribution names require bounded letters, digits, dots, hyphens, or underscores.");
            if (value is "sdk" or "jobs.workflow" || value.StartsWith("sdk.", StringComparison.Ordinal) ||
                value.StartsWith("jobs.workflow.", StringComparison.Ordinal))
                throw new InvalidDataException("Service contribution name is reserved by the execution host: " + value);
            if (!names.Add(value)) throw new InvalidDataException("Duplicate service contribution: " + value);
        }
    }

    private static ToolDescriptor FreezeTool(ToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.InputSchema.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(descriptor.InputSchema.GetRawText()) > 64 * 1024)
            throw new InvalidDataException("Service tool schema must be a JSON object of at most 64 KiB.");
        return descriptor with
        {
            InputSchema = descriptor.InputSchema.Clone(),
            PrerequisiteTools = Array.AsReadOnly(descriptor.PrerequisiteTools.ToArray()),
            ActivationTools = Array.AsReadOnly(descriptor.ActivationTools.ToArray())
        };
    }

    private static string ContainedPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative) || relative.Contains('\0'))
            throw new InvalidDataException("Service plugin assembly must be a relative package path.");
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Service plugin assembly leaves its package folder.");
        for (var current = full; current.Length >= fullRoot.Length; current = Path.GetDirectoryName(current)!)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Service plugin assembly paths cannot traverse linked package entries.");
            if (current == fullRoot) break;
        }
        return full;
    }

    internal static void ValidatePackageId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Length > 100 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new InvalidDataException("Service plugin IDs require 1-100 letters, digits, dots, hyphens, or underscores.");
    }

    private static string Error(Exception ex) => (ex is TargetInvocationException { InnerException: { } inner } ? inner : ex).Message;

    private static IResponseOutputHook Snapshot(string owner, IResponseOutputHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        var descriptor = Snapshot(owner, hook.Descriptor, OutputHookBoundary.ResponseBeforeCommit);
        return new ResponseHookSnapshot(hook, descriptor);
    }

    private static ICommittedOutputObserver Snapshot(string owner, ICommittedOutputObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var descriptor = Snapshot(owner, observer.Descriptor, OutputHookBoundary.OutputAfterCommit);
        return new OutputObserverSnapshot(observer, descriptor);
    }

    private static OutputHookDescriptor Snapshot(string owner, OutputHookDescriptor? descriptor, OutputHookBoundary boundary)
    {
        if (descriptor is null || descriptor.Boundary != boundary ||
            !descriptor.Id.StartsWith(owner + ":", StringComparison.Ordinal))
            throw new InvalidDataException("Response/output hook identity and boundary must belong to its service package.");
        return new(descriptor.Id, descriptor.Version, descriptor.Boundary,
            descriptor.Before, descriptor.After, descriptor.Timeout);
    }

    public void Dispose()
    {
        ServiceWorkflowContextRegistration[] retiring;
        LoadedPlugin[] retiringPlugins;
        lock (contextContributionGate)
        {
            if (disposed) return;
            Volatile.Write(ref disposed, true);
            retiring = contextContributions.ToArray();
            contextContributions.Clear();
            retiringPlugins = loaded.ToArray();
            // Close subscriptions before dropping the current-instance registry. This
            // is metadata only; no cancellation, package callback or teardown runs here.
            foreach (var item in retiringPlugins) item.Host.CloseCommittedRootUserInputSubscription();
            committedRootUserInputSettlement = Task.WhenAll(retiringPlugins
                .Where(RetainsInputLease)
                .Select(item => item.Host.CommittedRootUserInputRetirement));
        }
        // Preserve the existing context lifetime boundary before any observer package
        // can dispose/unload, including when it has no input-observation leases.
        foreach (var contribution in retiring) contribution.Retire();
        // Observer and settings-refresh callbacks can ignore cancellation. Queue
        // package teardown only after their actual host leases finish.
        foreach (var item in retiringPlugins.Reverse())
            if (RetainsInputLease(item))
                item.Host.RetireCommittedRootUserInput(() => DisposeObserverLoadedPlugin(item));
        foreach (var item in retiringPlugins.Reverse())
            if (!RetainsInputLease(item)) DisposeLoadedPlugin(item);
        lock (contextContributionGate) { loaded.Clear(); commands.Clear(); settingsPolicies.Clear(); }
    }

    private static bool RetainsInputLease(LoadedPlugin item) =>
        item.Plugin is IServiceCommittedRootUserInputObserver or IServiceOwnSettingsRefresh;

    private static void DisposeLoadedPlugin(LoadedPlugin item)
    {
        item.Host.Dispose();
        try { item.Plugin.Dispose(); } catch { }
        item.Context.Unload();
    }

    // Observer retirement reports each failed boundary and attempts all three steps.
    // Raw package/diagnostic exceptions may contain private values and are never retained.
    private static void DisposeObserverLoadedPlugin(LoadedPlugin item)
    {
        var failures = new List<string>(3);
        try { item.Host.Dispose(); }
        catch (Exception) { failures.Add("observer-host-dispose-failed"); }
        try { item.Plugin.Dispose(); }
        catch (Exception) { failures.Add("observer-plugin-dispose-failed"); }
        try { item.Context.Unload(); }
        catch (Exception) { failures.Add("observer-context-unload-failed"); }
        if (failures.Count != 0) throw new CommittedRootUserInputRetirementException(failures);
    }

    private sealed class CommittedRootUserInputRetirementException(IReadOnlyList<string> codes)
        : InvalidOperationException("Input observer package teardown failed: " + string.Join(", ", codes))
    {
        internal IReadOnlyList<string> Codes { get; } = Array.AsReadOnly(codes.ToArray());
    }

    private static bool IsCurrentLoadedPlugin(LoadedPlugin item, ServicePluginPackageBinding current) =>
        current.Loaded && current.Id == item.PackageBinding.Id && current.Version == item.PackageBinding.Version &&
        current.Generation == item.PackageBinding.Generation && current.Dependencies.SequenceEqual(item.PackageBinding.Dependencies);

    // Core attaches this private adapter after constructing the existing runtime.
    // Only host-frozen metadata is read while either runtime or subscription gate is held.
    private sealed class CommittedRootUserInputRuntimeGate(ServicePluginRuntime owner) : ICommittedRootUserInputDeliveryGate
    {
        public IReadOnlyList<CommittedRootUserInputObserverTarget> EligibleObservers(CommittedRootUserInputSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            lock (owner.contextContributionGate)
            {
                if (owner.disposed) return [];
                var result = new List<CommittedRootUserInputObserverTarget>();
                foreach (var item in owner.loaded.OrderBy(item => item.Host.PluginId, StringComparer.Ordinal))
                {
                    if (item.Plugin is not IServiceCommittedRootUserInputObserver ||
                        !owner.bindings.TryGetValue(item.Host.PluginId, out var binding) || !IsCurrentLoadedPlugin(item, binding) ||
                        string.IsNullOrEmpty(binding.Generation) ||
                        !item.Host.HasCommittedRootUserInputSubscription(source.ProfileId)) continue;
                    result.Add(new(binding.Id, binding.Generation, item.InstanceToken));
                    if (result.Count == CommittedRootUserInputDelivery.MaximumObserverModules) break;
                }
                return Array.AsReadOnly(result.ToArray());
            }
        }

        public ICommittedRootUserInputCallbackLease? TryAcquire(
            CommittedRootUserInputObserverTarget target, CommittedRootUserInputSource source)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(source);
            LoadedPlugin? item;
            IServiceCommittedRootUserInputObserver? observer;
            lock (owner.contextContributionGate)
            {
                if (owner.disposed || !owner.bindings.TryGetValue(target.PackageId, out var binding) ||
                    !binding.Loaded || binding.Generation != target.Generation) return null;
                item = owner.loaded.SingleOrDefault(candidate => candidate.Host.PluginId == target.PackageId &&
                    ReferenceEquals(candidate.InstanceToken, target.InstanceToken));
                observer = item?.Plugin as IServiceCommittedRootUserInputObserver;
                if (item is null || observer is null || !IsCurrentLoadedPlugin(item, binding)) return null;
            }
            ICommittedRootUserInputCallbackLease? Acquire(ScopedPluginSettingsAdmissionView? view)
            {
                lock (owner.contextContributionGate)
                {
                    if (owner.disposed || !owner.bindings.TryGetValue(target.PackageId, out var current) ||
                        !current.Loaded || current.Generation != target.Generation ||
                        !owner.loaded.Any(candidate => ReferenceEquals(candidate, item)) ||
                        !IsCurrentLoadedPlugin(item, current)) return null;
                    return item.Host.TryAcquireCommittedRootUserInput(observer, source, view);
                }
            }
            // The generic observer facet has no central-settings dependency.
            // Host acquisition rechecks the mode under its metadata gate, so a
            // concurrent generic-to-source-bound switch refuses this null view.
            if (!item.Host.HasRevisionBoundInputSubscription) return Acquire(null);
            // For source-bound mode, StateStore precedes runtime and host gates.
            // The sealed view contains metadata only; no package callback or
            // arbitrary settings getter runs under those gates.
            return item.Host.WithSourceBoundSettingsView<ICommittedRootUserInputCallbackLease?>(
                view => Acquire(view));
        }
    }

    private static int ContextDescriptorBytes(WorkflowContextContributionDescriptor descriptor,
        WorkflowToolContextProjectionDescriptor? projection) => checked(
            Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(descriptor, ContextDescriptorJson)) +
            (projection is null ? 0 : Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(projection, ContextDescriptorJson))));

    private static WorkflowContextContributionDescriptor FreezeContextDescriptor(ServicePluginPackageBinding binding,
        WorkflowContextContributionDescriptor descriptor)
    {
        if (descriptor is null || descriptor.SchemaVersion is not (WorkflowContextContributionContract.SchemaVersion or
                WorkflowToolContextProjectionContract.RequiredContributionDescriptorSchemaVersion) ||
            descriptor.Id is null || descriptor.Id.Length > 256 || !descriptor.Id.StartsWith(binding.Id + ":", StringComparison.Ordinal) ||
            !Enum.IsDefined(descriptor.HistoryProjection) || descriptor.Provider is null || descriptor.LegacyFormats is null)
            throw new InvalidDataException("Context contribution descriptor is invalid.");
        var local = descriptor.Id[(binding.Id.Length + 1)..];
        if (local.Length == 0 || local[0] is not (>= 'a' and <= 'z') ||
            local.Any(value => value is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')) ||
            local.Split(['.', '_', '-']).Any(value => value.Length == 0))
            throw new InvalidDataException("Context contribution identity must belong to its package.");
        var provider = descriptor.Provider;
        if (provider.Id is null || provider.Id.Length > 256 || provider.Description is null || provider.Description.Length > 4096 ||
            provider.SupportedReferenceKinds is null || provider.SupportedReferenceKinds.Count > Cerebral.Contracts.Context.ContextProviderContract.MaximumReferenceKinds)
            throw new InvalidDataException("Context contribution provider metadata is invalid.");
        var kinds = provider.SupportedReferenceKinds.Take(Cerebral.Contracts.Context.ContextProviderContract.MaximumReferenceKinds + 1).ToArray();
        if (kinds.Length != provider.SupportedReferenceKinds.Count || kinds.Length > Cerebral.Contracts.Context.ContextProviderContract.MaximumReferenceKinds)
            throw new InvalidDataException("Context contribution provider kinds exceed their bound.");
        var formats = descriptor.LegacyFormats.Take(WorkflowContextContributionContract.MaximumLegacyFormats + 1).ToArray();
        if (formats.Length != descriptor.LegacyFormats.Count || formats.Length > WorkflowContextContributionContract.MaximumLegacyFormats)
            throw new InvalidDataException("Context legacy formats exceed their bound.");
        var editions = new HashSet<int>();
        var frozen = new List<WorkflowContextLegacyFormat>();
        foreach (var format in formats)
        {
            if (format is null || format.SnapshotVersion < 1 || !editions.Add(format.SnapshotVersion) ||
                format.Fields is null || format.Fields.Count is < 1 or > WorkflowContextContributionContract.MaximumLegacyFields)
                throw new InvalidDataException("Context legacy format metadata is invalid.");
            var fields = format.Fields.Take(WorkflowContextContributionContract.MaximumLegacyFields + 1).ToArray();
            if (fields.Length != format.Fields.Count || fields.Length > WorkflowContextContributionContract.MaximumLegacyFields ||
                fields.Any(field => string.IsNullOrWhiteSpace(field) || field.Length > 128 || field.Any(char.IsControl) ||
                    new UTF8Encoding(false, true).GetByteCount(field) > 512) ||
                fields.Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Length)
                throw new InvalidDataException("Context legacy fields are invalid.");
            // Reserved field vocabulary and supported snapshot editions are host-owned policy.
            frozen.Add(new(format.SnapshotVersion, Array.AsReadOnly(fields)));
        }
        return descriptor with
        {
            Provider = provider with { SupportedReferenceKinds = Array.AsReadOnly(kinds) },
            LegacyFormats = Array.AsReadOnly(frozen.ToArray())
        };
    }

    internal bool IsCurrentContribution(object instanceToken, ServicePluginPackageBinding expected)
    {
        lock (contextContributionGate)
            return !disposed && loaded.Any(value => ReferenceEquals(value.InstanceToken, instanceToken)) &&
                bindings.TryGetValue(expected.Id, out var current) && current.Loaded &&
                current.Version == expected.Version && current.Generation == expected.Generation &&
                current.Dependencies.SequenceEqual(expected.Dependencies);
    }

    private sealed record LoadedPlugin(PluginLoadContext Context, IServicePlugin Plugin, Host Host,
        IReadOnlyList<IToolExtension> Tools, IReadOnlyList<string> ToolNames, IWorkflowModule? WorkflowModule,
        IReadOnlyList<IResponseOutputHook> ResponseHooks, IReadOnlyList<ICommittedOutputObserver> OutputObservers,
        IReadOnlyList<IWorkflowAuditAdapter> WorkflowAuditors, object InstanceToken,
        LoadedWorkflowPortableSettings? WorkflowPortableSettings, ServicePluginPackageBinding PackageBinding);
    private sealed class Candidate(string package, ServicePluginManifest manifest, string generation)
    {
        public string Package { get; } = package;
        public ServicePluginManifest Manifest { get; } = manifest;
        public string Generation { get; } = generation;
        public string? Error { get; set; }
    }
    private sealed record WorkflowModuleSnapshot(string Id, IReadOnlyList<IWorkflowDefinition> Workflows,
        IReadOnlyList<IWorkflowCommandHandler> Commands) : IWorkflowModule;
    private sealed record ToolExtensionSnapshot(IToolExtension Inner, IReadOnlyList<ToolDescriptor> Tools) : IToolExtension, IManagedInternetToolProvider
    {
        // Preserve the provider's explicit web-gate capability through the existing descriptor snapshot.
        // An older/unaware provider stays version zero and cannot acquire a raw-network fallback.
        public int ManagedInternetVersion => Inner is IManagedInternetToolProvider provider
            ? provider.ManagedInternetVersion : 0;

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken) =>
            Inner.ExecuteAsync(call, context, cancellationToken);
    }
    private sealed record ResponseHookSnapshot(IResponseOutputHook Inner, OutputHookDescriptor Descriptor) : IResponseOutputHook
    {
        public ValueTask<ResponseOutputHookResult> InvokeAsync(ResponseOutputHookContext context, CancellationToken cancellationToken) =>
            Inner.InvokeAsync(context, cancellationToken);
    }
    private sealed record OutputObserverSnapshot(ICommittedOutputObserver Inner, OutputHookDescriptor Descriptor) : ICommittedOutputObserver
    {
        public ValueTask<OutputObserverResult> ObserveAsync(CommittedOutputContext context, CancellationToken cancellationToken) =>
            Inner.ObserveAsync(context, cancellationToken);
    }
    private sealed partial class Host(string pluginId, string packageDirectory, string dataDirectory,
        IReadOnlyList<ServicePluginDependencyBinding> dependencies, EventHub events,
        IScopedPluginSettings? settings, Action<string>? settingsCommitted = null) : IServicePluginHost,
            IServiceEngineDiagnosticsHost, IServiceScalarGenerationHostV1, IServicePluginSettingsHost,
            IServiceCommittedRootUserInputHost, IServiceSourceBoundBackgroundHost, IDisposable
    {
        private readonly Action<string>? settingsCommittedCallback = settingsCommitted;
        private ScopedPluginSettingsAccess? sourceBoundSettingsAccess => settings as ScopedPluginSettingsAccess;
        private EventHub sourceBoundEvents => events;
        private readonly object diagnosticGate = new();
        private readonly List<IEngineDiagnosticSubscription> diagnosticLeases = [];
        private readonly List<IScalarGenerationSubscriptionV1> scalarGenerationLeases = [];
        private bool stopped;
        private readonly object inputObservationGate = new();
        private HashSet<string> inputObservationProfiles = new(StringComparer.Ordinal);
        private bool inputObservationEnabled;
        private long? sourceBoundExpectedSettingsRevision;
        private bool inputObservationRetired;
        private int inputObservationLeases;
        private Action? inputObservationCleanup;
        private bool inputObservationCleanupStarted;
        private readonly TaskCompletionSource inputObservationRetirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task CommittedRootUserInputRetirement => inputObservationRetirement.Task;

        public void SetCommittedRootUserInputSubscription(bool enabled, IReadOnlyList<string> sourceProfileIds)
        {
            ArgumentNullException.ThrowIfNull(sourceProfileIds);
            var profiles = new List<string>();
            var membership = new HashSet<string>(StringComparer.Ordinal);
            // A caller owns this collection and its enumerator. Read at most1025
            // entries and never execute its Count/getters/enumerator under host gates.
            foreach (var id in sourceProfileIds)
            {
                if (profiles.Count == 1024)
                    throw new ArgumentException("Input observation subscription exceeds its metadata ID bound.", nameof(sourceProfileIds));
                if (string.IsNullOrEmpty(id) || id.Length > 128 || id == "*" ||
                    id.Any(value => char.IsWhiteSpace(value) || char.IsControl(value)) || !membership.Add(id))
                    throw new ArgumentException("Input observation requires unique exact bounded profile IDs without wildcards, whitespace or control characters.", nameof(sourceProfileIds));
                profiles.Add(id);
            }
            if (enabled && profiles.Count == 0)
                throw new ArgumentException("An enabled input observation subscription requires an exact source profile.", nameof(sourceProfileIds));
            if (JsonSerializer.SerializeToUtf8Bytes(new { enabled, sourceProfileIds = profiles.ToArray() }, CerebralJson.Options).Length >
                SettingsPreferenceContract.MaximumPluginValuesUtf8Bytes)
                throw new ArgumentException("Input observation subscription exceeds its serialized metadata byte bound.", nameof(sourceProfileIds));
            lock (inputObservationGate)
            {
                ObjectDisposedException.ThrowIf(inputObservationRetired, this);
                inputObservationProfiles = membership;
                inputObservationEnabled = enabled;
                sourceBoundExpectedSettingsRevision = null;
            }
        }

        internal bool HasCommittedRootUserInputSubscription(string sourceProfileId)
        {
            lock (inputObservationGate)
                return !inputObservationRetired && inputObservationEnabled && inputObservationProfiles.Contains(sourceProfileId);
        }

        internal ICommittedRootUserInputCallbackLease? TryAcquireCommittedRootUserInput(
            IServiceCommittedRootUserInputObserver observer, CommittedRootUserInputSource source,
            ScopedPluginSettingsAdmissionView? settingsView)
        {
            lock (inputObservationGate)
            {
                if (inputObservationRetired || !inputObservationEnabled ||
                    !inputObservationProfiles.Contains(source.ProfileId)) return null;
                if (sourceBoundExpectedSettingsRevision is { } expected)
                {
                    if (settingsView is null || settingsView.Revision != expected ||
                        !settingsView.HasActive) return null;
                }
                else if (settingsView is not null)
                    return null; // The mode changed after the settings read.
                var lease = new CommittedRootUserInputCallbackLease(this, observer, source);
                inputObservationLeases++;
                return lease;
            }
        }

        internal void CloseCommittedRootUserInputSubscription()
        {
            lock (inputObservationGate)
            {
                inputObservationRetired = true;
                inputObservationEnabled = false;
                inputObservationProfiles.Clear();
            }
        }

        internal void RetireCommittedRootUserInput(Action cleanup)
        {
            Action? ready;
            lock (inputObservationGate)
            {
                inputObservationRetired = true;
                inputObservationEnabled = false;
                inputObservationProfiles.Clear();
                inputObservationCleanup ??= cleanup;
                ready = TakeCommittedRootUserInputCleanup();
            }
            QueueCommittedRootUserInputCleanup(ready);
        }

        private Action? TakeCommittedRootUserInputCleanup()
        {
            if (!inputObservationRetired || inputObservationLeases != 0 ||
                inputObservationCleanupStarted || inputObservationCleanup is null) return null;
            inputObservationCleanupStarted = true;
            var cleanup = inputObservationCleanup;
            inputObservationCleanup = null;
            return cleanup;
        }

        private void ReleaseCommittedRootUserInputLease()
        {
            Action? ready;
            lock (inputObservationGate)
            {
                inputObservationLeases--;
                ready = TakeCommittedRootUserInputCleanup();
            }
            QueueCommittedRootUserInputCleanup(ready);
        }

        private void QueueCommittedRootUserInputCleanup(Action? cleanup)
        {
            if (cleanup is null) return;
            // No package teardown or cancellation handler runs on a host-gate stack.
            _ = Task.Run(() =>
            {
                try { cleanup(); inputObservationRetirement.TrySetResult(); }
                catch (CommittedRootUserInputRetirementException error)
                {
                    inputObservationRetirement.TrySetException(error);
                }
                catch (Exception)
                {
                    inputObservationRetirement.TrySetException(new CommittedRootUserInputRetirementException(
                        ["observer-retirement-cleanup-failed"]));
                }
            });
        }

        private sealed class CommittedRootUserInputCallbackLease(Host owner,
            IServiceCommittedRootUserInputObserver observer, CommittedRootUserInputSource source)
            : ICommittedRootUserInputCallbackLease
        {
            private readonly object gate = new();
            private Host? currentOwner = owner;
            private IServiceCommittedRootUserInputObserver? currentObserver = observer;
            private bool invoked;
            private bool completed;
            private bool disposeRequested;

            public ValueTask InvokeAsync(CommittedRootUserInputObservation observation, CancellationToken token)
            {
                ArgumentNullException.ThrowIfNull(observation);
                IServiceCommittedRootUserInputObserver callback;
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposeRequested, this);
                    if (invoked) throw new InvalidOperationException("An input observer lease can invoke only once.");
                    if (observation.Source != source)
                        throw new InvalidOperationException("The input observation does not match its acquired source.");
                    invoked = true;
                    callback = currentObserver!;
                }
                return InvokeCoreAsync(callback, observation, token);
            }

            private async ValueTask InvokeCoreAsync(IServiceCommittedRootUserInputObserver callback,
                CommittedRootUserInputObservation observation, CancellationToken token)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    await callback.ObserveCommittedRootUserInputAsync(observation, token).ConfigureAwait(false);
                }
                finally
                {
                    Host? release;
                    lock (gate) { completed = true; release = TakeOwnerIfFinished(); }
                    release?.ReleaseCommittedRootUserInputLease();
                }
            }

            private Host? TakeOwnerIfFinished()
            {
                if (!disposeRequested || invoked && !completed) return null;
                var release = currentOwner;
                currentOwner = null;
                currentObserver = null;
                return release;
            }

            public void Dispose()
            {
                Host? release;
                lock (gate)
                {
                    if (disposeRequested) return;
                    disposeRequested = true;
                    release = TakeOwnerIfFinished();
                }
                release?.ReleaseCommittedRootUserInputLease();
            }
        }

        public IScopedPluginSettings Settings => new HostSettingsAccess(this,
            settings ?? throw new InvalidOperationException("Package settings storage is unavailable."));
        private sealed class HostSettingsAccess(Host host, IScopedPluginSettings inner) : IScopedPluginSettings, IScopedPluginSettingsTransfer
        {
            public PluginSettingsState Get(string scope = SettingsPreferenceContract.GlobalScope, string? scopeId = null)
            {
                lock (host.diagnosticGate)
                {
                    ObjectDisposedException.ThrowIf(host.stopped, host);
                    return inner.Get(scope, scopeId);
                }
            }
            public PluginSettingsSaveResult Save(int settingsVersion, JsonElement values, long expectedRevision,
                string scope = SettingsPreferenceContract.GlobalScope, string? scopeId = null)
            {
                PluginSettingsSaveResult result;
                lock (host.diagnosticGate)
                {
                    ObjectDisposedException.ThrowIf(host.stopped, host);
                    result = inner.Save(settingsVersion, values, expectedRevision, scope, scopeId);
                }
                if (result.Changed && scope == SettingsPreferenceContract.GlobalScope)
                    host.settingsCommittedCallback?.Invoke(host.PluginId);
                return result;
            }
            public PluginSettingsTransferState GetTransfer(string scope = SettingsPreferenceContract.GlobalScope, string? scopeId = null)
            {
                lock (host.diagnosticGate)
                {
                    ObjectDisposedException.ThrowIf(host.stopped, host);
                    if (inner is not IScopedPluginSettingsTransfer transfer)
                        throw new NotSupportedException("Package settings transfer requires a version-2 settings host.");
                    return transfer.GetTransfer(scope, scopeId);
                }
            }
            public PluginSettingsTransferSaveResult SaveTransfer(PluginSettingsTransferSaveRequest request)
            {
                PluginSettingsTransferSaveResult result;
                lock (host.diagnosticGate)
                {
                    ObjectDisposedException.ThrowIf(host.stopped, host);
                    if (inner is not IScopedPluginSettingsTransfer transfer)
                        throw new NotSupportedException("Package settings transfer requires a version-2 settings host.");
                    result = transfer.SaveTransfer(request);
                }
                if (result.Changed && request.Save.Document.Scope == SettingsPreferenceContract.GlobalScope)
                    host.settingsCommittedCallback?.Invoke(host.PluginId);
                return result;
            }
        }
        public IEngineDiagnosticSubscription OpenEngineDiagnostics()
        {
            lock (diagnosticGate)
            {
                ObjectDisposedException.ThrowIf(stopped, this);
                diagnosticLeases.RemoveAll(lease => lease is HostDiagnosticLease { Disposed: true });
                if (diagnosticLeases.Count >= 2) throw new InvalidOperationException("The package diagnostic subscription limit was reached.");
                var lease = new HostDiagnosticLease(events.SubscribeDiagnostics());
                diagnosticLeases.Add(lease); return lease;
            }
        }
        public IScalarGenerationSubscriptionV1 OpenScalarGenerationObservations()
        {
            lock (diagnosticGate)
            {
                ObjectDisposedException.ThrowIf(stopped, this);
                scalarGenerationLeases.RemoveAll(lease => lease is HostScalarGenerationLease { Disposed: true });
                if (scalarGenerationLeases.Count >= 2)
                    throw new InvalidOperationException("The package scalar generation subscription limit was reached.");
                var lease = new HostScalarGenerationLease(events.SubscribeScalarGeneration());
                scalarGenerationLeases.Add(lease);
                return lease;
            }
        }
        public void Dispose()
        {
            CloseCommittedRootUserInputSubscription();
            lock (diagnosticGate)
            {
                if (stopped) return; stopped = true;
                foreach (var lease in diagnosticLeases) lease.Dispose();
                diagnosticLeases.Clear();
                foreach (var lease in scalarGenerationLeases) lease.Dispose();
                scalarGenerationLeases.Clear();
            }
        }
        private sealed class HostDiagnosticLease(IEngineDiagnosticSubscription inner) : IEngineDiagnosticSubscription
        {
            private int disposed;
            public bool Disposed => Volatile.Read(ref disposed) != 0;
            public long DroppedRecords => inner.DroppedRecords;
            public IAsyncEnumerable<EngineDiagnosticRecord> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);
            public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) inner.Dispose(); }
        }
        private sealed class HostScalarGenerationLease(IScalarGenerationSubscriptionV1 inner) : IScalarGenerationSubscriptionV1
        {
            private int disposed;
            public bool Disposed => Volatile.Read(ref disposed) != 0;
            public long DroppedRecords => inner.DroppedRecords;
            public IAsyncEnumerable<ScalarGenerationObservationV1> ReadAsync(CancellationToken cancellationToken = default) =>
                inner.ReadAsync(cancellationToken);
            public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) inner.Dispose(); }
        }
        public string PluginId { get; } = pluginId;
        public string PackageDirectory { get; } = packageDirectory;
        public string DataDirectory { get; } = dataDirectory;
        public IReadOnlyList<ServicePluginDependencyBinding> Dependencies { get; } = dependencies;
        public void Publish(string eventType, string? jobId, object? data) => events.Publish(eventType, jobId, data);
    }
    private sealed class PluginLoadContext(string assemblyPath, string packageDirectory) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver resolver = new(assemblyPath);
        private static readonly HashSet<string> FrameworkNames = FrameworkAssemblyNames();
        protected override Assembly? Load(AssemblyName name)
        {
            // Both public SDK assemblies retain host identity across plugin load contexts.
            foreach (var shared in new[] { typeof(IServicePlugin).Assembly, typeof(IWorkflowModule).Assembly })
            {
                if (name.Name != shared.GetName().Name) continue;
                if (name.Version?.Major != shared.GetName().Version?.Major)
                    throw new InvalidDataException("The plugin references an incompatible Cerebral service SDK.");
                return shared;
            }
            var path = resolver.ResolveAssemblyToPath(name);
            if (path is not null) return LoadFromAssemblyPath(ContainedPath(packageDirectory, Path.GetRelativePath(packageDirectory, path)));
            var local = ContainedPath(packageDirectory, name.Name + ".dll");
            if (File.Exists(local)) return LoadFromAssemblyPath(local);
            if (name.Name is not null && FrameworkNames.Contains(name.Name)) return null;
            // Returning null for a private reference would bind accidentally to a host assembly.
            throw new FileNotFoundException("Package dependency assembly is missing from its own package: " + name.Name);
        }
        protected override nint LoadUnmanagedDll(string name)
        {
            var path = resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? 0 : LoadUnmanagedDllFromPath(ContainedPath(packageDirectory, Path.GetRelativePath(packageDirectory, path)));
        }

        private static HashSet<string> FrameworkAssemblyNames()
        {
            var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            var coreDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            return ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator)
                .Where(path => !string.IsNullOrEmpty(path))
                .Where(path => !path.StartsWith(baseDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                    Path.GetDirectoryName(path) == coreDirectory && (Path.GetFileNameWithoutExtension(path).StartsWith("System.", StringComparison.Ordinal) ||
                    Path.GetFileNameWithoutExtension(path) is "System" or "netstandard" or "mscorlib" or "Microsoft.CSharp" or "Microsoft.VisualBasic"))
                .Select(path => Path.GetFileNameWithoutExtension(path)).ToHashSet(StringComparer.Ordinal);
        }
    }
}

/// <summary>Trusted host-only registration. Frozen metadata survives retirement; package callbacks do not.</summary>
internal sealed class ServiceWorkflowContextRegistration
{
    private readonly GuardedContribution guarded;
    private readonly IWorkflowContextContribution contribution;
    internal ServicePluginPackageBinding PackageBinding { get; }
    internal IWorkflowContextContribution Contribution => contribution;
    internal bool IsAvailable => guarded.IsAvailable;
    internal string? RetirementFailure => guarded.RetirementFailure;
    internal void EnsureAvailable() => guarded.EnsureAvailable();
    internal void Retire() => guarded.Retire();

    internal ServiceWorkflowContextRegistration(ServicePluginRuntime owner, object instanceToken,
        ServicePluginPackageBinding binding, IWorkflowContextContribution inner,
        WorkflowContextContributionDescriptor descriptor, WorkflowToolContextProjectionDescriptor? projectionDescriptor = null)
    {
        if (WorkerToolContextProjectionAdmission.IsReceipt(descriptor) !=
            (inner is IWorkflowToolContextProjectionContribution) ||
            WorkerToolContextProjectionAdmission.IsReceipt(descriptor) != (projectionDescriptor is not null))
            throw new InvalidDataException("Receipt facet registration does not match the admitted contribution.");
        PackageBinding = binding;
        guarded = new(owner, instanceToken, binding, inner, descriptor);
        contribution = projectionDescriptor is null ? guarded : new GuardedProjectionContribution(guarded, projectionDescriptor);
    }

    // A facade over the same lifetime owner; it never retains a second package callback after retirement.
    private sealed class GuardedProjectionContribution(GuardedContribution guarded,
        WorkflowToolContextProjectionDescriptor projection) : IWorkflowToolContextProjectionContribution
    {
        public WorkflowContextContributionDescriptor Descriptor => guarded.Descriptor;
        public WorkflowToolContextProjectionDescriptor ProjectionDescriptor { get; } = projection;
        public Task<WorkflowContextSelection> CaptureAsync(WorkflowContextFacts facts, CancellationToken token) =>
            guarded.CaptureAsync(facts, token);
        public void Validate(WorkflowContextFacts accepted, WorkflowContextFacts current,
            WorkflowContextSelection selection, CancellationToken token) => guarded.Validate(accepted, current, selection, token);
        public Task<ContextProviderRequest> RestoreAsync(WorkflowContextAcceptedFacts facts,
            WorkflowContextSelection selection, CancellationToken token) => guarded.RestoreAsync(facts, selection, token);
        public WorkflowContextSelection ReadLegacy(WorkflowContextLegacyFacts facts, CancellationToken token) =>
            guarded.ReadLegacy(facts, token);
        public Task<WorkflowToolContextProjectionResolution> ProjectAsync(
            WorkflowToolContextProjectionRequest request, CancellationToken token) => guarded.ProjectAsync(request, token);
    }

    private sealed class GuardedContribution(ServicePluginRuntime owner, object instanceToken,
        ServicePluginPackageBinding binding, IWorkflowContextContribution inner,
        WorkflowContextContributionDescriptor descriptor) : IWorkflowContextContribution
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource lifetime = new();
        private IWorkflowContextContribution? callback = inner;
        private bool retired;
        public WorkflowContextContributionDescriptor Descriptor { get; } = descriptor;
        internal string? RetirementFailure { get; private set; }
        internal bool IsAvailable
        {
            get { lock (gate) return !retired && callback is not null && owner.IsCurrentContribution(instanceToken, binding); }
        }
        internal void EnsureAvailable()
        {
            lock (gate) Check();
        }
        private void Check()
        {
            if (retired || callback is null || !owner.IsCurrentContribution(instanceToken, binding))
                throw new InvalidOperationException("The captured package context contribution is unavailable.");
        }
        private (IWorkflowContextContribution Callback, CancellationTokenSource Linked) Enter(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                Check();
                return (callback!, CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token));
            }
        }
        private void Complete(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (gate) Check();
        }
        private async Task<T> InvokeAsync<T>(Func<IWorkflowContextContribution, CancellationToken, Task<T>> invoke,
            CancellationToken token)
        {
            var entered = Enter(token);
            using var linked = entered.Linked;
            // Package code and cancellation callbacks never execute under the lifetime gate.
            var result = await invoke(entered.Callback, linked.Token).ConfigureAwait(false);
            Complete(linked.Token);
            return result;
        }
        private T Invoke<T>(Func<IWorkflowContextContribution, CancellationToken, T> invoke, CancellationToken token)
        {
            var entered = Enter(token);
            using var linked = entered.Linked;
            var result = invoke(entered.Callback, linked.Token);
            Complete(linked.Token);
            return result;
        }
        public Task<WorkflowContextSelection> CaptureAsync(WorkflowContextFacts facts, CancellationToken token) =>
            InvokeAsync((value, cancellation) => value.CaptureAsync(facts, cancellation), token);
        public void Validate(WorkflowContextFacts accepted, WorkflowContextFacts current,
            WorkflowContextSelection selection, CancellationToken token) =>
            _ = Invoke((value, cancellation) => { value.Validate(accepted, current, selection, cancellation); return true; }, token);
        public Task<Cerebral.Contracts.Context.ContextProviderRequest> RestoreAsync(WorkflowContextAcceptedFacts facts,
            WorkflowContextSelection selection, CancellationToken token) =>
            InvokeAsync((value, cancellation) => value.RestoreAsync(facts, selection, cancellation), token);
        public WorkflowContextSelection ReadLegacy(WorkflowContextLegacyFacts facts, CancellationToken token) =>
            Invoke((value, cancellation) => value.ReadLegacy(facts, cancellation), token);
        internal Task<WorkflowToolContextProjectionResolution> ProjectAsync(
            WorkflowToolContextProjectionRequest request, CancellationToken token) =>
            InvokeAsync((value, cancellation) => (value as IWorkflowToolContextProjectionContribution
                ?? throw new InvalidDataException("The admitted contribution no longer exposes its receipt facet."))
                .ProjectAsync(request, cancellation), token);
        internal void Retire()
        {
            lock (gate)
            {
                if (retired) return;
                retired = true;
                callback = null;
            }
            // Already entered callbacks are cooperative. Late results are rejected; teardown does not promise draining.
            try { lifetime.Cancel(); }
            catch (AggregateException) { RetirementFailure = "context-cancellation-handler-failed"; }
            finally { lifetime.Dispose(); }
        }
    }
}
