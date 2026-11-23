using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop;
using Microsoft.Win32;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private sealed class AgentRow
    {
        public AgentRow(AgentProfile record, ModelRecord? model, string resolvedModelId,
            string executionMachine, string inferenceMachine, bool followsConfiguredDefault)
        {
            Record = record; Name = record.Name; Id = record.Id;
            Role = FriendlyAgentRole(record.Role);
            Model = string.IsNullOrWhiteSpace(resolvedModelId) ? "No configured model" : model?.Name ?? resolvedModelId;
            Binding = record.FollowsDefaultModel ? "Follows default" : "Pinned";
            ExecutionMachine = executionMachine; InferenceMachine = inferenceMachine;
            ConfiguredSlots = model?.ParallelRequests.ToString("N0") ?? "Not inspected";
            ModelRequirements = string.Join(" + ", ModelCapabilityPolicy.Requirements(record)
                .Select(id => id == ModelCapabilityIds.ImageInput ? "Image understanding" : ModelCapabilityIds.DisplayName(id)));
            Budgets = record.FollowsModelBudgets ? "Automatic" : "Custom";
            Tokens = $"{FormatTokens(record.ContextTokens)} context · {FormatTokens(record.ResponseTokens)} response";
            if (record.FollowsDefaultModel && !followsConfiguredDefault) Model += " (unresolved default)";
        }
        public AgentProfile Record { get; }
        public string Name { get; }
        public string Id { get; }
        public string Role { get; }
        public string Model { get; private set; }
        public string Binding { get; }
        public string ExecutionMachine { get; }
        public string InferenceMachine { get; }
        public string ConfiguredSlots { get; }
        public string ModelRequirements { get; }
        public string Budgets { get; }
        public string Tokens { get; }
    }

    private AgentRow CreateAgentRow(AgentProfile profile)
    {
        var modelId = profile.FollowsDefaultModel ? settings.DefaultModelId : profile.ModelId;
        var workerId = profile.FollowsDefaultModel ? settings.DefaultModelWorkerId : profile.ModelWorkerId;
        var model = computeLibraries.TryGetValue(workerId, out var catalog) && catalog.Connection == queueConnectionEpoch
            ? catalog.Models.FirstOrDefault(x => x.Id == modelId) : null;
        return new AgentRow(profile, model, modelId, ModelWorkerName(profile.WorkerId), ModelWorkerName(workerId),
            !string.IsNullOrWhiteSpace(settings.DefaultModelId));
    }

    private sealed record RuntimePlacement(long? WorkingSetMb, int? ResolvedGpuLayers, int? TotalGpuLayers, ModelCapacityPlan? CapacityPlan);

    private sealed class ModelRow
    {
        private readonly bool isDefault;
        public ModelRow(ModelRecord record, bool isDefault, int? assignedAgents = null)
        {
            Record = CerebralJson.Read<ModelRecord>(CerebralJson.Element(record));
            this.isDefault = isDefault;
            AssignedAgents = assignedAgents;
        }
        public ModelRecord Record { get; }
        private int? AssignedAgents { get; }
        public string Name => Record.Name;
        public string Purpose => string.IsNullOrWhiteSpace(Record.Purpose) ? "General" : Record.Purpose;
        public string Architecture => string.IsNullOrWhiteSpace(Record.Architecture) ? "Unknown" : Record.Architecture;
        public string Quantization => string.IsNullOrWhiteSpace(Record.Quantization) ? "Unknown" : Record.Quantization;
        public string Type => Architecture + " · " + Quantization;
        public string Capabilities => CapabilitySummary(Record);
        public string Size => FormatSize(Record.SizeBytes);
        public string Context => FormatContext(Record);
        public string Capacity => Record.AutomaticContextSizing ? "Fitted when loaded; unobserved" : FormatCapacity(ModelCapacity.Estimate(Record));
        public string AssignedProfiles => AssignedAgents?.ToString("N0") ?? "Not observed";
        public string ConfiguredSlots => Record.ParallelRequests.ToString("N0");
        public string State => (isDefault ? "Default · " : "") + FriendlyState(Record.State);
    }
    private async Task AddModels()
    {
        if (refreshingModelLibrary || loadedModelWorkerId != SelectedModelWorkerId) throw new InvalidOperationException("Wait for the selected machine's model library before adding a model.");
        if (loadedModelWorkerId != "local") { await AddRemoteModels(); return; }
        const string scan = "Scan the configured model folder";
        var local = Client.IsLocalPipe ? "Import a GGUF file" : "Import or upload a GGUF file";
        const string huggingFace = "Browse Hugging Face";
        const string direct = "Download from a direct URL";
        const string package = "Import a Cerebral model package";
        var form = new FieldsDialog(this, "Add models", "Choose where the model already lives. Scanning and importing register weights without loading them into memory.", "Continue") { Height = 350 };
        form.Choice("source", "Model source", [scan, local, huggingFace, direct, package], scan);
        if (form.ShowDialog() != true) return;
        switch (form.Selection("source"))
        {
            case scan: await ScanModels(); break;
            case var selected when selected == local: await ImportModel(); break;
            case huggingFace: await BrowseHuggingFace(); break;
            case direct: await DownloadModel(); break;
            case package: await ImportPackage(); break;
        }
    }

    private Task UseSelectedModel() => UseModel(SelectedModel());

    private async Task UseModel(ModelRecord model)
    {
        var profiles = await Get<List<AgentProfile>>("agents.list");
        if (!profiles.Any(x => x.Role == "lead"))
        {
            SetStatus("Set up Cerebral to use the selected model…");
            if (loadedModelWorkerId != "local") throw new InvalidOperationException("Set up the Cerebral agents before selecting a remote default model.");
            await SetupOrchestrator(model.Id);
            return;
        }
        SetStatus($"Loading and validating {model.Name} before changing Pegasus Cerebral…");
        await Command("models.default.switch", new { modelId = model.Id, modelWorkerId = loadedModelWorkerId });
        await RefreshSettings(); await RefreshAgents(); await RefreshModels(); await RefreshInstructionTargets();
        SetStatus($"Pegasus Cerebral now uses {model.Name}. Default agents were updated to fit its context capacity.");
    }

    private async Task ToggleSelectedModel()
    {
        var model = SelectedModel();
        var action = model.State is "loaded" or "loading" ? "unload" : "load";
        await ModelCommand(action);
    }

    private async Task ModelDetailsOrMore()
    {
        const string details = "View detected model details";
        const string runtime = "Runtime and chat settings";
        const string idle = "Automatic unload delay";
        const string distribute = "Verify model on a worker";
        const string export = "Export a model package";
        const string replace = "Replace registered weights";
        const string remove = "Remove model registration";
        const string refresh = "Refresh the model library";
        var model = SelectedModel();
        var form = new FieldsDialog(this, model.Name, "Detected information is read locally from the GGUF header. Technical paths and manual controls remain available here.", "Continue") { Height = 370 };
        form.Choice("action", "Details or action", loadedModelWorkerId == "local" ? [details, runtime, idle, distribute, export, replace, remove, refresh] : [details, runtime, idle, refresh], details);
        if (form.ShowDialog() != true) return;
        switch (form.Selection("action"))
        {
            case details: await ShowModelDetails(model); break;
            case runtime: await EditModel(); break;
            case idle: await EditModelIdle(); break;
            case distribute: await DistributeModel(); break;
            case export: await ExportPackage(); break;
            case replace: await ReplaceModel(); break;
            case remove: await ModelCommand("delete"); break;
            case refresh: await RefreshModels(); break;
        }
    }

    private async Task EditModelIdle()
    {
        var connection = queueConnectionEpoch; var workerId = loadedModelWorkerId;
        var model = SelectedModel();
        var form = new FieldsDialog(this, "Automatic unload delay · " + model.Name,
            "Keep the model loaded during active work and short breaks. The delay begins after model requests and local accepted work finish. Zero keeps it loaded until explicit unload or shutdown; -1 follows this Worker's default. This setting can change while the model is loaded. Paired inference workers apply their own local idle policy.")
            .Add("idle", "Minutes without active work (-1 = worker default, 0 = never)", model.IdleMinutes.ToString());
        if (form.ShowDialog() != true) return;
        if (connection != queueConnectionEpoch || workerId != loadedModelWorkerId || workerId != SelectedModelWorkerId)
            throw new InvalidOperationException("The model connection changed. Reopen settings for the intended machine.");
        await ModelLibraryGet<ModelRecord>("models.idle.set", new { id = model.Id, idleMinutes = form.Number("idle", -1, 10080) }, workerId);
        await RefreshModels(); SetStatus("Automatic unload delay saved. The model was not unloaded.");
    }

    private async Task ShowModelDetails(ModelRecord model)
    {
        var connection = queueConnectionEpoch; var machineId = loadedModelWorkerId;
        var capture = CaptureComputeDiagnostics(machineId);
        model = CerebralJson.Read<ModelRecord>(CerebralJson.Element(model));
        if (!capture.ModelPins.TryGetValue(model.Id, out var modelPin) || modelPin != ComputeConfigurationPin(model))
            throw new InvalidOperationException("The selected model configuration changed. Refresh and reopen its details.");
        var defaultModelId = settings.DefaultModelId; var defaultWorkerId = settings.DefaultModelWorkerId;
        var localReservedMemoryMb = settings.ReservedMemoryMb;
        var dispatch = QueueDispatch();
        var profiles = await ReadAgentOwnerAsync<List<AgentProfile>>(dispatch, "agents.list", null, Lifetime.Token);
        var observed = await ReadComputeDiagnosticsAsync(capture, dispatch, Lifetime.Token);
        if (observed is not { } diagnostics || connection != queueConnectionEpoch || machineId != loadedModelWorkerId)
            throw new InvalidOperationException("The model configuration or connection changed. Refresh and reopen details for the intended machine.");
        var resources = diagnostics.TryGetProperty("resources", out var resourceData)
            ? CerebralJson.Read<ResourceInfo>(resourceData)
            : new ResourceInfo(0, null, []);
        var reservedMemoryMb = machineId == "local" ? localReservedMemoryMb
            : diagnostics.TryGetProperty("reservedMemoryMb", out var reservedValue) && reservedValue.ValueKind == JsonValueKind.Number
                && reservedValue.TryGetInt32(out var remoteReserve) && remoteReserve is >= 0 and <= 1_048_576
                ? remoteReserve : 0;
        var runtime = RuntimeForModel(diagnostics, model.Id);
        var assigned = ProfilesUsingModel(model, profiles, defaultModelId, machineId, defaultWorkerId).ToList();
        var estimate = ModelCapacity.Estimate(model);
        var template = TemplateDescription(model);
        var parameters = FormatParameters(model.ParameterCount);
        var native = model.NativeContextTokens is { } nativeTokens ? $"{nativeTokens:N0} tokens" : "Not detected";
        var warning = string.IsNullOrWhiteSpace(model.MetadataWarning) ? "None" : model.MetadataWarning;
        var error = string.IsNullOrWhiteSpace(model.Error) ? "None" : model.Error;
        var assignmentText = assigned.Count == 0 ? "No configured agents currently use this model." :
            string.Join("\n", assigned.Select(x => $"• {x.Name} · {(x.FollowsDefaultModel ? "follows the default" : "pinned")} · {(x.FollowsModelBudgets ? "automatic budgets" : "custom budgets")}"));
        Ui.ShowText(this, "Model details · " + model.Name,
            $"Location: {ModelWorkerName(machineId)}\nUse for Pegasus Cerebral: {(model.Id == defaultModelId && machineId == defaultWorkerId ? "Yes" : "No")}\n" +
            $"State: {FriendlyState(model.State)}\nPurpose: {model.Purpose}\nArchitecture: {model.Architecture}\nQuantization: {model.Quantization}\nParameters: {parameters}\nWeights: {FormatSize(model.SizeBytes)}\n\n" +
            CapabilityDetails(model) + "\n\n" +
            $"Context detected from GGUF: {native}\nConservative starting context: {model.RecommendedContextTokens:N0} tokens\nCurrent context per request: {model.ContextTokens:N0} tokens\n" +
            $"Context sizing: {(model.AutomaticContextSizing ? "Automatic from live memory" : "Fixed")}" + (model.AutomaticContextSizing ? $" · ceiling {(model.MaximumContextTokens > 0 ? model.MaximumContextTokens.ToString("N0") : "detected native")}" : "") + "\n" +
            $"Configured parallel slot maximum: {model.ParallelRequests:N0}\nEstimated configured context pool: {estimate.TotalContextTokens:N0} tokens\n" +
            $"Blocks: {model.BlockCount?.ToString("N0") ?? "Not detected"}\nEmbedding width: {model.EmbeddingLength?.ToString("N0") ?? "Not detected"}\nAttention heads: {model.AttentionHeadCount?.ToString("N0") ?? "Not detected"}\nKV heads: {model.AttentionHeadCountKv?.ToString("N0") ?? "Not detected"}\n\n" +
            CapacityDetails(model, estimate, resources, reservedMemoryMb, runtime) + "\n\n" +
            $"Assigned profiles ({assigned.Count:N0})\n{assignmentText}\n\nProfiles can create independent conversation contexts. When this model is loaded, its weights can serve multiple contexts. Assigned profiles do not prove working agents or available inference slots.\n\n" +
            $"Chat format: {template}\nGPU layers: {(model.GpuLayers < 0 ? "Automatic fit" : model.GpuLayers == 0 ? "CPU only" : model.GpuLayers.ToString("N0"))}\nIdle unload: {(model.IdleMinutes < 0 ? "Service default" : model.IdleMinutes == 0 ? "Never" : model.IdleMinutes + " minutes")}\n\n" +
            $"Metadata source: {model.MetadataSource}\nMetadata warning: {warning}\nRuntime error: {error}\n\n" +
            $"Publisher: {model.Publisher ?? "Not recorded"}\nUpstream model: {model.UpstreamModelId ?? "Not recorded"}\nOfficial model page: {model.UpstreamModelUrl ?? "Not recorded"}\n" +
            $"Artifact source: {model.SourceUrl ?? "Local or not recorded"}\nUpstream revision: {model.UpstreamRevision ?? "Not recorded"}\n" +
            $"License / terms: {model.License ?? "Not recorded"}\nLicense link: {model.LicenseUrl ?? "Not recorded"}\n" +
            $"Package: {model.PackageId ?? "Not recorded"} · {model.PackageVersion ?? "version not recorded"} · {model.PackagePurpose ?? "purpose not recorded"}{(model.StarterPackage ? " · starter package" : "")}\n" +
            $"Description: {model.Description ?? "Not recorded"}\nTags: {(model.Tags.Count == 0 ? "None" : string.Join(", ", model.Tags))}\nStored at: {model.Path}");
    }

    private static Dictionary<string, string> ModelChoices(IEnumerable<ModelRecord> models)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            var basic = $"{model.Name} · {(string.IsNullOrWhiteSpace(model.Purpose) ? "General" : model.Purpose)} · {(string.IsNullOrWhiteSpace(model.Quantization) ? "GGUF" : model.Quantization)} · {CapabilitySummary(model)}";
            var label = basic;
            for (var suffix = 2; result.ContainsKey(label); suffix++) label = $"{basic} ({suffix})";
            result[label] = model.Id;
        }
        return result;
    }

    private static int PreferredContext(ModelRecord model) =>
        Math.Clamp(model.RecommendedContextTokens > 0 ? model.RecommendedContextTokens : model.ContextTokens, 512, Math.Max(512, model.ContextTokens));

    private static int PreferredResponse(int contextTokens)
    {
        var maximum = AgentTokenLimits.MaximumVisibleResponse(contextTokens, "off", AgentTokenLimits.MinimumReasoningTokens);
        return Math.Clamp(contextTokens / 4, AgentTokenLimits.MinimumResponseTokens, Math.Min(3072, maximum));
    }

    private static string ClampAgentBudgets(AgentProfile profile, ModelRecord? model, int requestedContext, int requestedResponse)
    {
        profile.ContextTokens = requestedContext;
        profile.ResponseTokens = requestedResponse;
        var originalReasoningMode = profile.ReasoningMode;
        var originalReasoningTokens = profile.ReasoningTokens;
        var originalContext = profile.ContextTokens;
        var originalResponse = profile.ResponseTokens;
        if (model is not null)
        {
            var resolution = AgentBudgetPolicy.Resolve(profile, model);
            profile.ContextTokens = resolution.ContextTokens;
            profile.ResponseTokens = resolution.ResponseTokens;
            profile.ReasoningMode = resolution.ReasoningMode;
            profile.ReasoningTokens = resolution.ReasoningTokens;
        }
        else
        {
            profile.ContextTokens = Math.Clamp(profile.ContextTokens, AgentTokenLimits.MinimumContextTokens, 1_048_576);
            var maximumResponse = AgentTokenLimits.MaximumVisibleResponse(profile.ContextTokens, profile.ReasoningMode, profile.ReasoningTokens);
            if (maximumResponse < AgentTokenLimits.MinimumResponseTokens)
                throw new ArgumentException("The context and reasoning reserve leave no reliable response capacity.");
            profile.ResponseTokens = Math.Clamp(profile.ResponseTokens, AgentTokenLimits.MinimumResponseTokens, maximumResponse);
        }
        var adjustments = new List<string>();
        if (profile.FollowsModelBudgets) adjustments.Add($"Token budgets follow the model: {profile.ContextTokens:N0} context and {profile.ResponseTokens:N0} response.");
        else
        {
            if (profile.ContextTokens != originalContext) adjustments.Add($"Context was limited to {profile.ContextTokens:N0} tokens for this model.");
            if (profile.ResponseTokens != originalResponse) adjustments.Add($"Response was limited to {profile.ResponseTokens:N0} tokens so it fits the context.");
        }
        if (profile.ReasoningMode != originalReasoningMode || profile.ReasoningTokens != originalReasoningTokens) adjustments.Add("The reasoning budget was adjusted to fit the context.");
        return string.Join(' ', adjustments);
    }

    private static string ConciseModelSummary(ModelRecord model)
    {
        var estimate = ModelCapacity.Estimate(model);
        var native = model.NativeContextTokens is { } value ? $"{value:N0} detected" : "not detected";
        var warning = string.IsNullOrWhiteSpace(model.MetadataWarning) ? "" : "\n" + model.MetadataWarning;
        return $"{model.Purpose} · {FriendlyValue(model.Architecture)} · {FriendlyValue(model.Quantization)} · {FormatSize(model.SizeBytes)}\n" +
               $"Capabilities: {CapabilitySummary(model)}\n" +
               $"Context per request: {model.ContextTokens:N0} configured · {model.RecommendedContextTokens:N0} recommended · {native}\n" +
               $"Estimated configured pool: {estimate.TotalContextTokens:N0} context tokens across {model.ParallelRequests} configured request slot{(model.ParallelRequests == 1 ? "" : "s")} · {(model.AutomaticContextSizing ? "memory fitted when loaded" : FormatCapacity(estimate))}\n" +
               $"Chat format: {TemplateDescription(model)} · " +
               $"{(model.GpuLayers < 0 ? "automatic GPU fit" : model.GpuLayers == 0 ? "CPU only" : model.GpuLayers + " GPU layers")}{warning}";
    }

    private static IReadOnlyList<ModelCapabilityStatus> EffectiveCapabilityStatus(ModelRecord model) =>
        model.CapabilityStatus.Count == 0 ? ModelCapabilityPolicy.Assess(model) : model.CapabilityStatus;

    private static string CapabilitySummary(ModelRecord model)
    {
        var capabilities = EffectiveCapabilityStatus(model);
        var textCapability = capabilities.FirstOrDefault(x => x.Id == ModelCapabilityIds.TextGeneration);
        var text = textCapability?.Usable == true ? "Text available" : textCapability?.PayloadStatus switch
        {
            ModelCapabilityStates.Missing => "Text payload missing",
            ModelCapabilityStates.Unavailable => "Text payload unreadable",
            _ => "Text unavailable"
        };
        var image = capabilities.FirstOrDefault(x => x.Id == ModelCapabilityIds.ImageInput);
        return text + " · " + (image?.Usable == true ? "Image understanding verified" : image?.PayloadStatus == ModelCapabilityStates.Available
            ? "Image understanding unverified" : image?.FamilyStatus == ModelCapabilityStates.Potential
                ? "Image understanding needs component" : "Image understanding unavailable");
    }

    private static string CapabilityDetails(ModelRecord model)
    {
        var lines = new List<string> { "Model capabilities" };
        foreach (var capability in EffectiveCapabilityStatus(model))
        {
            lines.Add($"• {(capability.Id == ModelCapabilityIds.ImageInput ? "Image understanding" : ModelCapabilityIds.DisplayName(capability.Id))}: {(capability.Usable ? "Available now" : "Unavailable now")}");
            lines.Add($"  Family: {capability.FamilyStatus} · installed payload: {capability.PayloadStatus} · Cerebral runtime path: {capability.RuntimeStatus}");
            lines.Add($"  Evidence ({capability.EvidenceBasis}): {capability.Evidence}");
        }
        return string.Join('\n', lines);
    }

    private IEnumerable<AgentProfile> ProfilesUsingModel(ModelRecord model, IEnumerable<AgentProfile> profiles, string defaultModelId, string workerId) =>
        ProfilesUsingModel(model, profiles, defaultModelId, workerId, settings.DefaultModelWorkerId);

    private static IEnumerable<AgentProfile> ProfilesUsingModel(ModelRecord model, IEnumerable<AgentProfile> profiles,
        string defaultModelId, string workerId, string defaultModelWorkerId) =>
        profiles.Where(x => x.FollowsDefaultModel ? model.Id == defaultModelId && workerId == defaultModelWorkerId :
            x.ModelId == model.Id && x.ModelWorkerId == workerId)
            .OrderBy(x => x.Role == "lead" ? 0 : x.Role == "compactor" ? 2 : 1)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase);

    private static RuntimePlacement? RuntimeForModel(JsonElement diagnostics, string modelId)
    {
        if (!diagnostics.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in models.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id) || id.GetString() != modelId) continue;
            long? working = item.TryGetProperty("workingSetMb", out var workingValue) && workingValue.ValueKind == JsonValueKind.Number && workingValue.TryGetInt64(out var workingMb) ? workingMb : null;
            int? resolved = item.TryGetProperty("resolvedGpuLayers", out var resolvedValue) && resolvedValue.ValueKind == JsonValueKind.Number && resolvedValue.TryGetInt32(out var resolvedLayers) ? resolvedLayers : null;
            int? total = item.TryGetProperty("totalGpuLayers", out var totalValue) && totalValue.ValueKind == JsonValueKind.Number && totalValue.TryGetInt32(out var totalLayers) ? totalLayers : null;
            ModelCapacityPlan? plan = item.TryGetProperty("capacityPlan", out var planValue) && planValue.ValueKind == JsonValueKind.Object
                ? CerebralJson.Read<ModelCapacityPlan>(planValue)
                : null;
            return new(working, resolved, total, plan);
        }
        return null;
    }

    private static string CapacityDetails(ModelRecord model, ModelCapacityEstimate estimate, ResourceInfo resources, int reservedMemoryMb, RuntimePlacement? runtime)
    {
        var runtimeFitted = runtime?.CapacityPlan is { Automatic: true, MeasuredAvailableAfterLoadMb: not null };
        var placement = runtimeFitted
            ? "llama.cpp fitted this load from the model's actual architecture and device backend; the generic dense-attention estimate was not used for the final context choice."
            : estimate.FullPlacementMb is { } full
            ? $"Estimated full-placement working set: {FormatMemory(full)} ({FormatMemory(estimate.WeightsMb)} weights + {FormatMemory(estimate.KvCacheMb!.Value)} context cache + {FormatMemory(estimate.RuntimeReserveMb)} runtime allowance)."
            : $"Estimated minimum working set: {FormatMemory(estimate.MinimumWorkingSetMb)} ({FormatMemory(estimate.WeightsMb)} weights + {FormatMemory(estimate.RuntimeReserveMb)} runtime allowance). Context-cache metadata was unavailable.";
        var evidence = runtimeFitted
            ? "The post-load reading is measured device telemetry. Other GPU activity during startup can affect the observed difference."
            : "Planning estimate only; llama.cpp may use a different amount or offload part of the model to host memory.";
        if (model.GpuLayers == 0) evidence += " This model is configured for CPU placement.";
        var plan = runtime?.CapacityPlan ?? ModelCapacity.Plan(model, resources, reservedMemoryMb);
        var sizing = plan.Automatic
            ? $"Automatic context: {plan.ContextTokens:N0} tokens per request, up to {plan.MaximumContextTokens:N0}. " +
              (plan.AvailableMemoryMb is { } available ? $"The load plan observed {FormatMemory(available)} free in {plan.MemoryPool}. " : "") +
              (plan.EstimatedRemainingMb is { } remaining ? $"Estimated margin beyond the {FormatMemory(plan.ReservedMemoryMb)} reserve: {FormatMemory(Math.Max(0, remaining))}. " : "") + plan.Basis
            : $"Fixed context: {plan.ContextTokens:N0} tokens per request. {plan.Basis}";
        if (plan.MeasuredAvailableAfterLoadMb is { } measured)
            sizing += $" Post-load measurement: {FormatMemory(measured)} free; configured reserve {(plan.ReservedMemorySatisfied == true ? "satisfied" : "not satisfied")}.";
        if (plan.AvailableMemoryMb is { } before && plan.MeasuredAvailableAfterLoadMb is { } after)
            sizing += $" Observed device-memory change while loading: {FormatMemory(Math.Max(0, before - after))}.";
        var runtimeText = runtime is null
            ? "Current model process: not loaded on this service."
            : $"Current model process: {FormatNullableMemory(runtime.WorkingSetMb)} host working set" +
              (runtime.ResolvedGpuLayers is { } layers ? $" · llama.cpp reports {layers:N0}/{runtime.TotalGpuLayers?.ToString("N0") ?? "?"} layers offloaded" : " · resolved layer placement was not found in the bounded runtime log") + ".";

        if (resources.Gpus.FirstOrDefault() is not { } gpu || gpu.TotalMb is not { } total || gpu.FreeMb is not { } free)
            return $"Memory planning\n{sizing}\n{placement}\n{evidence}\n{runtimeText}\nCurrent GPU telemetry: unavailable. Current system memory available: {FormatNullableMemory(resources.SystemAvailableMb)}.";

        var used = Math.Max(0, total - free);
        var poolName = gpu.Unified ? "Current shared CPU/GPU memory pool" : "Current GPU memory";
        var projection = estimate.FullPlacementMb is not { } planned || model.State is "loaded" or "loading" || model.GpuLayers == 0
            ? model.State is "loaded" or "loading"
                ? "The model is loaded; total/free telemetry includes it, but this runtime does not yet attribute exact memory to one model process."
                : "No GPU projection is available for this placement."
            : free - planned - Math.Max(0, reservedMemoryMb) >= 0
                ? $"If fully placed from the current state, about {FormatMemory(free - planned - Math.Max(0, reservedMemoryMb))} would remain beyond the configured {FormatMemory(Math.Max(0, reservedMemoryMb))} reserve."
                : $"Full placement exceeds current free memory plus the configured reserve by about {FormatMemory(Math.Abs(free - planned - Math.Max(0, reservedMemoryMb)))}; automatic fitting may offload layers to host memory or loading may fail.";
        var hostLine = gpu.Unified ? "System and GPU figures describe the same unified pool and are counted once." : $"Current system memory available: {FormatNullableMemory(resources.SystemAvailableMb)}.";
        return $"Memory planning\n{sizing}\n{placement}\n{evidence}\n{runtimeText}\n{poolName}: {FormatMemory(used)} used · {FormatMemory(free)} free · {FormatMemory(total)} total.\n{projection}\n{hostLine}";
    }

    private static string FormatCapacity(ModelCapacityEstimate estimate) => estimate.FullPlacementMb is { } full
        ? $"~{FormatMemory(full)} estimated full placement"
        : $"~{FormatMemory(estimate.MinimumWorkingSetMb)}+ estimated working set";

    private static string FormatNullableMemory(long? value) => value is { } memory ? FormatMemory(memory) : "unavailable";
    private static string FormatMemory(long value) => value >= 1024 ? $"{value / 1024d:0.0} GiB" : $"{value:N0} MiB";

    private static string TemplateDescription(ModelRecord model) => !string.IsNullOrWhiteSpace(model.ChatTemplate)
        ? "configured override"
        : model.HasEmbeddedChatTemplate ? "embedded in the GGUF" : "not detected; configure one before use";

    private static string FormatContext(ModelRecord model)
    {
        var native = model.NativeContextTokens is { } detected && detected != model.ContextTokens ? $" · {FormatTokens(detected)} detected" : "";
        var total = (long)Math.Max(1, model.ParallelRequests) * model.ContextTokens;
        var pool = model.ParallelRequests > 1 ? $" · {FormatTokensLong(total)} total" : "";
        return $"{(model.AutomaticContextSizing ? "Auto " : "Fixed ")}{FormatTokens(model.ContextTokens)}/request{pool}{native}";
    }

    private static string FormatTokens(int value) => value >= 1_000_000 ? $"{value / 1_000_000d:0.#}M" : value >= 1000 ? $"{value / 1000d:0.#}K" : value.ToString("N0");
    private static string FormatTokensLong(long value) => value >= 1_000_000 ? $"{value / 1_000_000d:0.#}M" : value >= 1000 ? $"{value / 1000d:0.#}K" : value.ToString("N0");
    private static string FormatSize(long bytes) => bytes <= 0 ? "Unknown" : bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GiB" : $"{bytes / (double)(1L << 20):0} MiB";
    private static string FormatParameters(long? count) => count is null or <= 0 ? "Not detected" : count >= 1_000_000_000 ? $"{count.Value / 1_000_000_000d:0.##}B" : $"{count.Value / 1_000_000d:0.#}M";
    private static string FriendlyValue(string value) => string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
    private static string FriendlyState(string value) => string.IsNullOrWhiteSpace(value) ? "Unknown" : char.ToUpperInvariant(value[0]) + value[1..];

    private async Task ScanModels(Action? ensureCurrent = null)
    {
        await RefreshSettings();
        ensureCurrent?.Invoke();
        SetStatus("Scanning the configured model folder without copying files…");
        var scan = await Get<ModelRepositoryScan>("models.scan");
        ensureCurrent?.Invoke();
        if (scan.Models.Count == 0)
        {
            Ui.ShowText(this, "Model repository", scan.Directory + "\n\nNo GGUF models found. Choose your existing repository in Settings → Folders & runtime.\n" + string.Join("\n", scan.Issues));
            return;
        }
        var choices = scan.Models.Where(x => x.Error is null && x.RegisteredId is null).ToDictionary(x => x.Name + " · " + x.Size + " · " + Path.GetRelativePath(scan.Directory, x.Path), x => x);
        if (choices.Count == 0)
        {
            Ui.ShowText(this, "Model repository", scan.Directory + "\n\n" + string.Join("\n", scan.Models.Select(x => x.Name + " · " + x.Status)) + "\n" + string.Join("\n", scan.Issues)); return;
        }
        var form = new FieldsDialog(this, "Discovered models", scan.Directory + "\nRegister a file in place. This neither copies weights nor loads GPU memory.", "Import selected") { Height = 430 };
        form.Choice("model", "Available GGUF model", choices.Keys, choices.Keys.First());
        form.Check("all", "Register all discovered single-file GGUF models", false);
        if (form.ShowDialog() != true) return;
        foreach (var item in form.Checked("all") ? choices.Values.ToList() : [choices[form.Selection("model")]])
        {
            ensureCurrent?.Invoke();
            await Command("models.import", new { path = item.Path, name = item.Name, copy = false });
        }
        ensureCurrent?.Invoke();
        await RefreshModels(); SetStatus("Discovered models registered in place." + (scan.Truncated ? " Scan reached its 512-file limit." : ""));
    }

    private async Task BrowseHuggingFace(Action? ensureCurrent = null)
    {
        await RefreshSettings();
        ensureCurrent?.Invoke();
        var query = new FieldsDialog(this, "Hugging Face models", "Public GGUF repositories are supported. The publisher's model license applies independently of Cerebral. Gated repositories can be imported from an authorized local file.", "List model files") { Height = 380 };
        query.Add("repository", "Repository ID or URL", "unsloth/Qwen3.5-9B-GGUF").Add("revision", "Revision", "main");
        if (query.ShowDialog() != true) return;
        ensureCurrent?.Invoke();
        SetStatus("Reading publisher model metadata…");
        var listing = await Get<HuggingFaceModelList>("models.huggingface", new { repository = query.Value("repository"), revision = query.Value("revision") });
        ensureCurrent?.Invoke();
        if (listing.Files.Count == 0) throw new InvalidOperationException("No supported single-file GGUF models were found.");
        var choices = listing.Files.Where(x => !Path.GetFileName(x.FileName).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)).ToDictionary(x => x.Label, x => x);
        if (choices.Count == 0) throw new InvalidOperationException("The repository contains only projectors, not a text model.");
        var projectors = listing.Files.Where(x => Path.GetFileName(x.FileName).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)).ToDictionary(x => x.Label, x => x);
        var recommended = choices.FirstOrDefault(x => x.Value.FileName.Equals("Qwen3.5-9B-Q5_K_M.gguf", StringComparison.OrdinalIgnoreCase)).Key ?? choices.Keys.First();
        var noProjector = "Text only (do not download an image component)";
        var projectorChoices = new[] { noProjector }.Concat(projectors.Keys).ToArray();
        var recommendedProjector = projectors.FirstOrDefault(x => Path.GetFileName(x.Value.FileName).Equals("mmproj-BF16.gguf", StringComparison.OrdinalIgnoreCase)).Key ?? noProjector;
        var select = new FieldsDialog(this, "Download model", $"Repository: {listing.Repository}\nRevision: {listing.Revision}\nLicense: {listing.License ?? "See publisher model card"}\nDestination: {settings.ModelDirectory}\nChoose the matching image component from the same publisher package when this model should accept images. Files download once and remain unloaded until requested.", "Download") { Height = 570 };
        select.Choice("file", "Model file and download size", choices.Keys, recommended);
        select.Choice("projector", "Image component", projectorChoices, recommendedProjector);
        if (select.ShowDialog() != true) return;
        ensureCurrent?.Invoke();
        var selected = choices[select.Selection("file")];
        var file = Path.GetFileName(selected.FileName);
        SetStatus("Downloading " + file + "…");
        var model = await Get<ModelRecord>("models.download", new { url = selected.Url, name = Path.GetFileNameWithoutExtension(file), fileName = file, license = listing.License });
        ensureCurrent?.Invoke();
        var projectorChoice = select.Selection("projector");
        if (projectorChoice != noProjector)
        {
            var component = projectors[projectorChoice];
            SetStatus("Downloading matching image component…");
            ensureCurrent?.Invoke();
            model = await Get<ModelRecord>("models.component.download", new { id = model.Id, url = component.Url, fileName = model.Id + ".projector.gguf" });
            ensureCurrent?.Invoke();
        }
        await RefreshModels(); SetStatus(model.VisionProjectorPath is null ? "Publisher download completed and text model registered." : "Publisher model and matching image component registered.");
    }
    private Task ImportPackage() => ImportModelPackageCore(null);
    internal Task ImportModelPackage(string path) => ImportModelPackageCore(path);

    private async Task ImportModelPackageCore(string? requestedPath)
    {
        string path;
        if (requestedPath is null)
        {
            var form = new FieldsDialog(this, "Import model package", "Select a Cerebral model package accessible on the service machine. It carries one GGUF plus portable runtime settings, provenance and license metadata.", "Review package")
                .Add("path", "Package path on service machine", "")
                .ActionButton("Browse this computer…", f =>
                {
                    var picker = new OpenFileDialog { Filter = "Cerebral model package|*.CerebralLLM;*.cerebral-model.zip", CheckFileExists = true };
                    if (picker.ShowDialog(f) == true) f.SetValue("path", picker.FileName);
                });
            if (form.ShowDialog() != true) return;
            path = form.Value("path");
        }
        else path = requestedPath;
        path = Path.GetFullPath(path);
        if (!ModelPackageContract.IsPackagePath(path) || !File.Exists(path))
            throw new InvalidDataException($"Select an existing {ModelPackageContract.PrimaryExtension} package.");
        var package = InspectModelPackage(path);
        if (!Confirm($"Import {package.Model.Name}?\n\nPackage: {package.Package.Id} {package.Package.Version}\nSettings: {package.Package.SettingsMode}\nModel: {package.Model.Architecture} · {package.Model.Quantization}\nLicense: {package.Model.License ?? "See publisher metadata"}\nPackage size: {new FileInfo(path).Length / 1073741824d:N2} GiB\n\nWeights are stored in the package without recompression. The model will be copied into the configured repository and remain unloaded.")) return;
        SetStatus("Importing model package…");
        await Command("models.package.import", new { path });
        await RefreshModels(); SetStatus("Model package imported. The model is unloaded.");
    }
    private async Task ExportPackage()
    {
        await RefreshSettings();
        var model = SelectedModel();
        var file = string.Concat(model.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ModelPackageContract.PrimaryExtension;
        var form = new FieldsDialog(this, "Export model package", "This explicit export creates one portable package. GGUF weights and the optional image projector are stored without recompression; small metadata is compressed. Machine paths, loaded state and transient errors are excluded.", "Export package")
            .Choice("settings", "Portable settings", ["Current model settings", "Safe defaults"], "Current model settings")
            .Add("path", "Destination on service machine", Path.Combine(settings.SharedDirectory, file))
            .ActionButton("Choose destination on this computer…", f =>
            {
                var picker = new SaveFileDialog { Filter = "Cerebral model package|*.CerebralLLM", FileName = file, OverwritePrompt = true };
                if (picker.ShowDialog(f) == true) f.SetValue("path", picker.FileName);
            });
        if (form.ShowDialog() != true) return;
        SetStatus("Exporting model package…");
        await Command("models.package.export", new { id = model.Id, path = form.Value("path"), settingsMode = form.Selection("settings") == "Safe defaults" ? "defaults" : "current" });
        SetStatus("Model package exported to " + form.Value("path"));
    }

    private static ModelPackage InspectModelPackage(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count is < 2 or > 3 || archive.Entries.Count(x => x.FullName == ModelPackageContract.ManifestName) != 1)
            throw new InvalidDataException("A model package requires one manifest, weights, and at most one image projector.");
        var entry = archive.GetEntry(ModelPackageContract.ManifestName)!;
        if (entry.Length > 65_536) throw new InvalidDataException("Model package manifest exceeds 64 KiB.");
        using var input = entry.Open(); using var buffer = new MemoryStream(); input.CopyTo(buffer);
        if (buffer.Length > 65_536) throw new InvalidDataException("Expanded model package manifest exceeds 64 KiB.");
        using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.EnumerateObject().GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1))
            throw new InvalidDataException("Model package manifest must be one JSON object with unique fields.");
        var package = CerebralJson.Read<ModelPackage>(document.RootElement);
        ModelPackageContract.ValidateLayout(package, archive.Entries.Select(x => x.FullName));
        return package;
    }
    private async Task ReplaceModel()
    {
        var model = SelectedModel();
        var form = new FieldsDialog(this, "Replace model weights", "Replacement preserves the configured model ID and agent assignments. The model must be unloaded and have no active work. Runtime compatibility is checked on the next load.", "Replace weights")
            .Add("path", "Replacement GGUF path on service machine", "")
            .ActionButton("Browse this computer…", f =>
            {
                var picker = new OpenFileDialog { Filter = "GGUF model|*.gguf" };
                if (picker.ShowDialog(f) == true) f.SetValue("path", picker.FileName);
            });
        if (form.ShowDialog() != true || !Confirm("Replace the weights at " + model.Path + "? The complete replacement is staged first; no backup copy is retained.")) return;
        SetStatus("Staging replacement model…");
        await Command("models.replace", new { id = model.Id, path = form.Value("path") });
        await RefreshModels(); SetStatus("Model weights replaced. Load to check runtime compatibility.");
    }
}
