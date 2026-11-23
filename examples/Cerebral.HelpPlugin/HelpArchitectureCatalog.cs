using System.Collections.ObjectModel;

namespace Cerebral.HelpPlugin;

public sealed record HelpArchitectureNode(string Id, string Title, string Caption, string Owner,
    string Current, string Target, string Source, string PageId, string SectionId,
    string Side, int Row, string? ParentId = null)
{
    public string ReferenceId => "n:" + Id;
    public string Kind => Id is "journal" or "private-store" or "settings-core" ? "store"
        : Id == "outcomes-core" ? "decision"
        : ParentId == "core" || Id == "context" ? "process"
        : Id is "chat" or "config" or "mcp" or "cortex" or "hosted" ? "external"
        : "component";
}
public sealed record HelpArchitectureEdge(string Id, string From, string To, string FromPort, string ToPort,
    string Label, string Payload, string Contract, string Status, string FailureCancellation,
    string Source, string PageId, string SectionId, bool Proposed = false)
{
    public string ReferenceId => "e:" + Id;
}

/// <summary>
/// Passive architecture references. Core is a stable library/framework boundary, not a claimed
/// separate running process. Explicit immutable edges represent actual caller/return ends.
/// </summary>
public static class HelpArchitectureCatalog
{
    public const string ContentVersion = HelpCatalog.ContentVersion;
    public static IReadOnlyList<HelpArchitectureNode> Nodes { get; } = Array.AsReadOnly(new[]
    {
        N("core", "Framework / host Core", "Identity · grants · lifecycle · durable effects", "Core / host",
            "Core is a library boundary. Current command/scheduler/model/domain composition is hosted in Cerebral.Worker.",
            "Stable generic framework; selected engine and packages supply policy/domain behavior.",
            "docs/CODING-GUIDANCE.md:10-48; src/Cerebral.Worker/Program.cs:171-228", "framework", "contract-map", "center", 0),
        N("chat", "Chat / UI", "Orchestrator · client · native presentation", "Core / Chat owner",
            "Client commands/events use the authenticated local pipe to the current combined Worker/host.",
            "Native shell remains usable independently of optional engine/plugins.",
            "src/Cerebral.Orchestrator/OrchestratorExplicitWorkflows.cs:30; src/Cerebral.Worker/LocalWorkerPipeServer.cs:147-245", "framework", "contract-map", "left", 0),
        N("engine", "Selected engine", "Strategy · next decision · context selection", "Engine owner",
            "IEnginePlugin receives host-issued versioned strategy requests; some conversation/context composition still resides in the host.",
            "Replaceable engine owns conversation strategy and policy; host owns generic admitted execution.",
            "src/Cerebral.Engine.Contracts/EngineContracts.cs:6-38; docs/CODING-GUIDANCE.md:18-28", "engine", "contract-map", "left", 1),
        N("plugins", "Plugins", "Domain tools · context hooks · native mounts", "Plugin owners",
            "Desktop/service SDK contributions exist. Host invokes scoped tool/context callbacks and consumes public projections.",
            "Plugin-private stores, schemas and migrations remain inside each package.",
            "src/Cerebral.Core/ServicePlugins.cs:170-190; src/Cerebral.Desktop.Plugins/DesktopMountContract.cs:22-92; docs/CODING-GUIDANCE.md:28-30", "plugins", "contract-map", "left", 2),
        N("cortex", "Cortex", "Reference retrieval · task continuity", "Cortex owners",
            "Cerebral's native Cortex route retrieves exact passive references and records bounded continuity.",
            "Separate data ownership and optional references; no direct foreign database access.",
            "src/Cerebral.Worker/CortexContext.cs:41; src/Cerebral.Worker/NativeCortexContinuity.cs:10", "cortex", "contract-map", "left", 3),
        N("mcp", "External MCP", "Permitted tool providers", "External provider / host adapter",
            "Host tool dispatch supports configured MCP tools; discovery does not grant permission.",
            "External capability uses the same admitted scope/outcome rules.",
            "src/Cerebral.Tools/ProviderTransport.cs:74; src/Cerebral.Tools/ToolDispatcher.cs:761; docs/CODING-GUIDANCE.md:44-50", "workflows", "contract-map", "left", 4),
        N("config", "Configuration", "Integrator · reviewed selection/staging", "Configurator / installation owners",
            "stage v1 and model controls return reviewed revision/status or NotSubmitted/Unknown. Missing-content transfer is absent.",
            "One admitted adapter/backend plus model provisioning workflow for local and remote Workers.",
            "src/Cerebral.Integrator/IntegratorServicePackages.cs:53; src/Cerebral.Worker/ModelDistribution.cs:95,131", "getting-started", "configure", "right", 0),
        N("local", "Local Worker boundary", "Current: co-hosted model runtime", "Worker / model runtime owner",
            "Current local model execution remains in the combined Worker host. Accepted local text routes through configured registered preparation; image/projector requests retain the legacy route. Configured admission still requires worker.local.llama.cpp; several runtime instances are supported in source.",
            "One model-facing Worker per machine, several adapters/resident models and one correlated multiplexed tunnel.",
            "src/Cerebral.Worker/WorkerModelAdapters.cs:34-48; src/Cerebral.Worker/ConfiguredWorkflowModelCoordinator.cs:237,286; src/Cerebral.Worker/ModelRuntime.cs:30-50; docs/CODING-GUIDANCE.md:20-26", "workers", "machine", "right", 1),
        N("remote", "Remote Worker machine", "Current: HTTP / prepared NDJSON", "Remote Worker / runtime owner",
            "One generation per NDJSON response; already-installed exact model content can be verified. No complete binary distribution or multiplexed application tunnel.",
            "Same semantic model contract per machine; destination-specific backend payload and one multiplexed correlated tunnel.",
            "src/Cerebral.Worker/OwnerPreparedStreamingHttp.cs:122-147; src/Cerebral.Worker/ModelDistribution.cs:95,131", "workers", "stream", "right", 2),
        N("hosted", "Hosted providers", "Optional private / proposed integration", "Provider plugin / engine owners",
            "Vendor provider candidates and a generic hosted value contract are private; main-chat adoption/live calls are unexecuted.",
            "Declared provider capability/count/budget/stream outcomes without fake Worker/GGUF/GPU identities.",
            "docs/COMPONENT-COORDINATION.md; docs/CODING-GUIDANCE.md:10-34", "providers", "contract-map", "right", 3),
        N("input-core", "Command / event intake", "Scoped command, request and conversation identity", "Host client/command boundary",
            "CommandRequest/CommandResult and separate ServiceEvent subscription terminate at the current host dispatcher.",
            "Stable client contract stays independent of domain providers.",
            "src/Cerebral.Client/LocalWorkerClient.cs:50; src/Cerebral.Worker/LocalWorkerPipeServer.cs:211; src/Cerebral.Worker/CommandDispatcher.cs:92",
            "framework", "contract-map", "inside", 1, "core"),
        N("admission-core", "Agents / task admission", "Profile · grants · frozen run bindings", "Framework / workflow owner",
            "Workflow submission captures exact request/profile/definition/version and commits accepted run/input before dispatch.",
            "Accepted identities/revisions and uncertain work remain durable across engine changes.",
            "src/Cerebral.Client/WorkflowConversationClient.cs:100; src/Cerebral.Engine/Persistence/WorkflowStore.cs:38",
            "workflows", "contract-map", "inside", 2, "core"),
        N("engine-bridge", "Engine / context bridge", "Versioned evaluation and context-provider effects", "Host adapter / engine owner",
            "Host evaluates the selected engine with EngineRequest and validates the matching EngineDecision/transition; workflow context providers use scoped effects. WorkflowRegistry checks command/module descriptor schemas with WorkflowSchema's explicit JSON Schema subset; unsupported keywords reject registration. Model-response schema validation is a separate model/native boundary. These source contracts do not establish current runtime adoption.",
            "Strategy/policy stays in the engine; generic adapter and admission stay in framework.",
            "src/Cerebral.Engine/Activation/EngineController.cs:232; src/Cerebral.Engine/Workflows/WorkflowRuntime.cs:131,259; src/Cerebral.Engine/Workflows/WorkflowSchema.cs:16-20,24-98; src/Cerebral.Engine/Workflows/WorkflowRegistry.cs:22-74,94-118,171-179",
            "engine", "contract-map", "inside", 3, "core"),
        N("discovery-core", "Skill / capability discovery", "Permitted summaries → exact schema / guidance", "Host/catalog and skill owners",
            "The host exposes bounded permitted tool, skill and SDK-contract discovery through cerebral.host:catalog v1, skills.list/read, structured sdk.discover/describe and eligible plugin.contract. These outputs do not form a general owner/version/source/Help directory.",
            "Select exact discovery facts without loading all source; skills do not create grants/capabilities.",
            "src/Cerebral.Worker/WorkerWorkflowConversation.cs:102,374; src/Cerebral.Worker/SkillToolExtension.cs:9; src/Cerebral.Worker/WorkerWorkflows.cs:552,564; src/Cerebral.Tools/PluginScaffoldToolExtension.cs:59,648",
            "reference", "discovery", "inside", 4, "core"),
        N("dispatch-core", "Admitted tools / effects", "Workflow effect → tool / context / model contract", "Framework dispatcher / effect owner",
            "ToolDispatcher and workflow effects validate schemas/grants/bindings before scoped plugin, MCP, context or model execution.",
            "One owned admitted execution path; no direct plugin-to-model bypass.",
            "src/Cerebral.Tools/ToolDispatcher.cs:173; src/Cerebral.Worker/WorkerWorkflowEffects.cs:120; src/Cerebral.Engine/Workflows/WorkflowRuntime.cs:185",
            "workflows", "contract-map", "inside", 5, "core"),
        N("outcomes-core", "Known outcome?", "Complete / partial / rejected / uncertain", "Framework / effect owner",
            "Validated effect results update durable state. Unknown effects are held for reconciliation instead of replay.",
            "Engine next decision consumes the exact known outcome; cancellation intent alone is not completion.",
            "src/Cerebral.Engine/Workflows/WorkflowRuntime.cs:185,315; src/Cerebral.Contracts/Models/ModelRuntimeContracts.cs:115",
            "reference", "recovery", "inside", 6, "core"),
        N("publish-core", "Public output / commit", "Before-commit hooks → authoritative events", "Host response pipeline / UI projection",
            "Typed response hooks can continue/replace/hold/reject before commit; after-commit observers cannot rewrite authoritative events.",
            "Only public content/status/result projections flow back to Chat; private reasoning is not displayed.",
            "src/Cerebral.Contracts/Routing/ResponseOutputContracts.cs:47; src/Cerebral.Worker/ServicePluginRuntime.cs:218; src/Cerebral.Worker/Program.cs:228",
            "framework", "contract-map", "inside", 7, "core"),
        N("journal", "Durable execution", "Admission · intent · outcomes · receipts", "Framework / effect owner",
            "Accepted run/effect state is journaled before dispatch and updated throughout execution.",
            "Preserve accepted identities, revisions and uncertain outcomes across recovery.",
            "src/Cerebral.Engine/Persistence/WorkflowStore.cs:38-65; src/Cerebral.Engine/Workflows/WorkflowRuntime.cs:185,315", "framework", "contract-map", "inside", 0, "core"),
        N("settings-core", "Shared settings", "Source and retained owner evidence", "Core storage / Configurator / plugin schema owners",
            "SettingsPreferenceContract schema 1 source implements central cerebral.db global/agent plugin documents, renderer-owned layout and named presets. Retained Core/Configurator handoffs include private execution evidence. This authored map does not inspect current host/Source adoption.",
            "Core owns generic storage/revisions/presets/export-restore; plugins own settings meaning/schema/validation/migration; renderers own layout schema. Portable backup 14 and explicitly selected complete-nonsecret backup 15 are implemented at development tiers; legacy formats remain readable. Full docking/floating and unmigrated legacy packages remain pending.",
            "src/Cerebral.Core/SettingsPreferenceContracts.cs:7-87; src/Cerebral.Core/ConfigurationBackupContracts.cs:7-27; src/Cerebral.Core/CompleteSettingsContracts.cs:7-41; src/Cerebral.Worker/StateStore.cs:71; src/Cerebral.Worker/SettingsPreferencesStore.cs:40,111,396; build/Core/SettingsPresets/handoff.json; build/Configurator/SettingsPresets/handoff.json", "getting-started", "configure", "inside", 8, "core"),
        N("context", "Context strategy", "Task/history + admitted optional contributions", "Engine / context owner",
            "Planner selects bounded context and uses actual preparation counts; some host composition remains transitional.",
            "Normal context pipeline combines task/history and plugin-owned optional style.",
            "src/Cerebral.Engine/Context/ContextPlanner.cs:26; src/Cerebral.Conversation/ConversationContext.cs:45", "reference", "context-budget", "inside", 0, "engine"),
        N("private-store", "Private store / migrations", "Owned only by the plugin", "Plugin owner",
            "Package code owns private data interpretation and migration.",
            "Host consumes declared public projections; Core does not decode private stores.",
            "docs/CODING-GUIDANCE.md:28,38,50", "plugins", "contract-map", "inside", 0, "plugins"),
        N("context-hook", "Context contribution", "Optional Contracts.Context SPI · schema 1", "Plugin policy / generic host / Engine consumer",
            "The Persona package owns IWorkflowContextContribution applicability, selection and codecs. The generic host supplies accepted facts and preserves package association; Engine consumes the selected contribution through its normal context pipeline. Retained private fixtures cover capture, immutable selection and assembly through ContextPlanner.Compose. Their constructed requests/recording preparation remain distinct from actual Worker/native model behavior.",
            "Persona policy/data/presets/applicability stay package-owned and separate from model-worker transport. Historical confirmed failures and Waiting/Unknown effects retain their qualified outcomes; resource closure does not resolve uncertainty. Root checkpoint 751 records the baseline goal COMPLETE, with the Engine v3 diagnostic and update backlog separately open. No current native token/capacity/cache-affinity, voice or package-adoption result is inferred from the older Help fixtures.",
            "src/Cerebral.Contracts/Context/WorkflowContextContributions.cs:8-83; src/Cerebral.Worker/ServicePluginRuntime.cs:265; src/Cerebral.Worker/WorkerContextContributionHost.cs:133,209; build/Core/OwnerQuiescence/generic-context-handoff.json; examples/Cerebral.PersonalityPlugin/Service/PersonalityWorkflowContextContribution.cs:17,62,110; examples/Cerebral.PersonalityPlugin/Service/PersonalityLegacyContextReader.cs:62; build/Plugins/SettingsPresets/personality-adoption-handoff.json; examples/Cerebral.PersonalityPlugin/PersonalityEditor.cs; examples/Cerebral.PersonalityPlugin/PersonalityEditor.Pending.cs; src/Cerebral.Core/CompleteSettingsContracts.cs:45; build/Plugins/SettingsPresets/personality-editor-handoff.json; examples/Cerebral.PersonalityPlugin/Service/PersonalitySettingsTransferCommands.cs; build/Plugins/SettingsPresets/personality-current-transfer-handoff.json; build/Plugins/SettingsPresets/personality-editor-transfer-handoff.json; examples/Cerebral.PersonalityPlugin/Service/PersonalityWorkflowContextContribution.cs:77,106; src/Cerebral.Conversation/ConversationContext.cs:14,358; src/Cerebral.Conversation/ConversationContextContributions.cs; build/Core/OwnerQuiescence/conversation-context-consumer-handoff.json; build/Plugins/SettingsPresets/personality-worker-pipe-verification.json; src/Cerebral.Engine/Context/ContextPlanner.cs:175; build/Plugins/SettingsPresets/personality-provider-assembly-verification.json; build/Plugins/SettingsPresets/personality-provider-assembly-optional-receipt-runtime-terminal.json; build/Plugins/SettingsPresets/personality-provider-assembly-optional-receipt-runtime-review.json", "plugins", "contract-map", "inside", 1, "plugins"),
        N("output-hook", "Response output hook", "Typed presentation/content snapshots", "Plugin output owner",
            "Before-commit transform/hold/reject and after-commit observer callbacks are wired; observers cannot alter committed events.",
            "Declared output hook preserves authoritative source and presentation ownership.",
            "src/Cerebral.Contracts/Routing/ResponseOutputContracts.cs:47; src/Cerebral.Worker/ServicePluginRuntime.cs:218",
            "plugins", "contract-map", "inside", 3, "plugins"),
        N("native-mount", "Native mount", "sidebar.tabs / configuration.pages", "Plugin content / shell layout",
            "Descriptor/factory contributes a native view; top-document and full docking are absent.",
            "Generic shell layout supports adjustable/docked/floating placement.",
            "src/Cerebral.Desktop.Plugins/DesktopMountContract.cs:22-92", "plugins", "contract-map", "inside", 2, "plugins"),
        WorkerPart("local-adapter", "Backend adapter", "Roles · templates · tokenization", "local", 0, "adapter"),
        WorkerPart("local-model", "Resident model", "Asset + model/instance identity", "local", 1, "instance"),
        WorkerPart("local-slot", "Inference slot", "Owned capacity · one-use preparation", "local", 2, "instance"),
        WorkerPart("remote-adapter", "Backend adapter", "Remote model-specific preparation", "remote", 0, "adapter"),
        WorkerPart("remote-model", "Resident model", "Exact installed model/instance", "remote", 1, "instance"),
        WorkerPart("remote-slot", "Inference slot", "Owned capacity / cleanup outcome", "remote", 2, "instance")
    });

    public static IReadOnlyList<HelpArchitectureEdge> Edges { get; } = BuildEdges();

    private static ReadOnlyCollection<HelpArchitectureEdge> BuildEdges()
    {
        var edges = new List<HelpArchitectureEdge>();
        void Pair(string id, string a, string b, string port, string outgoing, string returning,
            string request, string response, string contract, string source, string page, string section,
            bool proposed = false, string? failure = null, string? statusOverride = null)
        {
            var status = statusOverride ?? (proposed ? "Private/proposed integration; no live adoption" : "Current source-supported boundary; not live runtime proof");
            var semantics = failure ?? "Cancellation is explicit; unavailable/rejected/partial/unknown remains distinct. Preserve accepted binding and uncertain effects; do not blindly replay.";
            edges.Add(new(id + ".request", a, b, port, port, outgoing, request, contract, status, semantics, source, page, section, proposed));
            edges.Add(new(id + ".return", b, a, port, port, returning, response, contract, status, semantics, source, page, section, proposed));
        }
        Pair("chat", "chat", "input-core", "UI", "submit", "events",
            "Conversation/task IDs, message/attachments and submit/steer/stop control.",
            "Conversation events, public fragments/status/usage, terminal result and revision to UI.",
            "Authenticated client/Worker command and event routes; desktop API 1 projection.",
            "src/Cerebral.Worker/LocalWorkerPipeServer.cs:147-245; src/Cerebral.Desktop.Plugins/DesktopPluginContract.cs:190-199",
            "framework", "contract-map");
        Pair("engine", "engine-bridge", "engine", "strategy", "evaluate", "decision",
            "EngineRequest: operation/work ID, expected revision, typed input/state/contract binding.",
            "EngineDecision: next state/output/effect requests; returned effect results inform the next evaluation.",
            "pegasus.cerebral.engine protocol 1; IEnginePlugin.EvaluateAsync.",
            "src/Cerebral.Engine.Contracts/EngineContracts.cs:6-38; src/Cerebral.Engine/Workflows/WorkflowRuntime.cs:49-74",
            "engine", "contract-map");
        Pair("tools", "dispatch-core", "plugins", "tools", "tool call", "outcome",
            "Admitted tool/owner operation and validated scoped input.",
            "Typed domain result, artifact/reference or rejected/uncertain outcome to host/engine.",
            "pegasus.cerebral.service API 1; IToolExtension / IServiceCommandExtension.",
            "src/Cerebral.Core/ServicePlugins.cs:170-190", "plugins", "contract-map");
        Pair("context", "dispatch-core", "context-hook", "context", "collect", "context",
            "Host-authored WorkflowContextFacts/AcceptedFacts and frozen package/provider association; prompts, history, profile instructions, paths and grants are excluded from the host facts.",
            "WorkflowContextSelection disposition, package-owned opaque payload/digest and body-free ContextProviderRequest template; restore uses the winning accepted selection.",
            "Cerebral.Contracts.Context; WorkflowContextContributionContract schema 1; IServiceWorkflowContextModule/IWorkflowContextContribution: CaptureAsync, Validate, RestoreAsync, pure ReadLegacy.",
            "src/Cerebral.Contracts/Context/WorkflowContextContributions.cs:8-83; src/Cerebral.Worker/WorkerExecutionSnapshot.cs:13; src/Cerebral.Worker/WorkerContextContributionHost.cs:133,209; build/Core/OwnerQuiescence/generic-context-handoff.json; examples/Cerebral.PersonalityPlugin/Service/PersonalityWorkflowContextContribution.cs:17,62; build/Plugins/SettingsPresets/personality-adoption-handoff.json; examples/Cerebral.PersonalityPlugin/PersonalityEditor.cs; examples/Cerebral.PersonalityPlugin/PersonalityEditor.Pending.cs; src/Cerebral.Core/CompleteSettingsContracts.cs:45; build/Plugins/SettingsPresets/personality-editor-handoff.json; examples/Cerebral.PersonalityPlugin/Service/PersonalitySettingsTransferCommands.cs; build/Plugins/SettingsPresets/personality-current-transfer-handoff.json; build/Plugins/SettingsPresets/personality-editor-transfer-handoff.json; examples/Cerebral.PersonalityPlugin/Service/PersonalityWorkflowContextContribution.cs:77,106; src/Cerebral.Conversation/ConversationContext.cs:14,358; src/Cerebral.Conversation/ConversationContextContributions.cs; build/Core/OwnerQuiescence/conversation-context-consumer-handoff.json; build/Plugins/SettingsPresets/personality-worker-pipe-verification.json; src/Cerebral.Engine/Context/ContextPlanner.cs:175; build/Plugins/SettingsPresets/personality-provider-assembly-verification.json; build/Plugins/SettingsPresets/personality-provider-assembly-optional-receipt-runtime-terminal.json; build/Plugins/SettingsPresets/personality-provider-assembly-optional-receipt-runtime-review.json", "plugins", "contract-map",
            failure:"Cancellation and Ready/Omitted/Unavailable/Blocked/Unsupported remain explicit. Corrupt ready selection refuses; preserve accepted origin, package-owned codec/digest and uncertain outcomes without fallback or replay. Personality historical v4 MigrationPending stays blocked. Legacy cleanup requires an explicit owner CAS/readback transition; ordinary capture does not guarantee per-dispatch legacy cleanliness.",
            statusOverride:"Private provider-to-assembled-request fixture PASSED 13 modes/194 ordered assertion labels, not 194 unique tests: four positive Compose cases, seven persisted confirmed failures and two retained Waiting/Unknown effects without adoption/planner. One executable invocation (exit0); 15 in-process runtime calls are source-derived 13+2 Omitted, not instrumented. Constructed host/settings/base request and recording preparation; no live model-facing insertion or adoption. Separate earlier312 capture proof and consumer172 fixtures are not added. Both prior oracle failures remain preserved; product policy was not weakened. Help read accepted records without rerunning them.");
        Pair("mount", "core", "native-mount", "mounts", "lifecycle", "view",
            "Native contribution lifecycle and host availability.",
            "Descriptor/factory/native view projection consumed by shell layout and shown in Chat/Configuration.",
            "pegasus.cerebral.desktop API 1; DesktopMountView; sidebar.tabs/configuration.pages.",
            "src/Cerebral.Desktop.Plugins/DesktopMountContract.cs:22-92", "plugins", "contract-map",
            failure:"Missing optional mount preserves shell. Dispose ends owner resources/subscriptions; top document/full docking is target work.");
        Pair("output", "publish-core", "output-hook", "response hooks", "snapshot", "decision",
            "Before-commit typed content/presentation snapshot; after-commit authoritative events to observer.",
            "Before-commit continue/replacement/hold/reject; after-commit diagnostics only. Committed output then flows to UI.",
            "ResponseOutputContracts; wired response output pipeline, not unwired generic request hooks.",
            "src/Cerebral.Contracts/Routing/ResponseOutputContracts.cs:47; src/Cerebral.Worker/ServicePluginRuntime.cs:218",
            "plugins", "contract-map", failure:"Hooks receive no model/tool/service/grant handle; observers cannot alter committed events.");
        Pair("cortex", "core", "cortex", "references", "query", "references",
            "Configured namespaces, bounded search/exact-read or authorized task checkpoint.",
            "Exact passive record/revision or asset reference and bounded checkpoint state to context/continuity.",
            "Supported native Cortex schema/guidance; separate product ownership.",
            "src/Cerebral.Worker/CortexContext.cs:41; src/Cerebral.Worker/NativeCortexContinuity.cs:10",
            "cortex", "contract-map", failure:"Missing Cortex is explicit safe degradation where allowed. Search previews select exact reads; retained content is not authorization or an execution receipt.");
        Pair("mcp", "dispatch-core", "mcp", "external tools", "tool call", "content",
            "Permitted configured MCP tool and bounded arguments/cancellation.",
            "Tool content/result or error to admitted effect outcome and the engine decision.",
            "Current host MCP adapter/tool dispatch; exact configured descriptor.",
            "src/Cerebral.Tools/ProviderTransport.cs:74; src/Cerebral.Tools/ToolDispatcher.cs:761", "workflows", "contract-map");
        Pair("config", "config", "core", "configuration", "configure", "receipt",
            "Reviewed manifestPath/expectedId/expectedRevision, selected machine/model and load/stage control.",
            "Reviewed package revision, capability/readiness status or NotSubmitted/Unknown receipt to Configuration.",
            "sdk.describe / service-packages.stage v1; current model commands/assets v1/v2.",
            "src/Cerebral.Integrator/IntegratorServicePackages.cs:53; src/Cerebral.Worker/ModelDistribution.cs:95,131",
            "getting-started", "configure", failure:"Guard captured owner before/after awaits; no resend after consumed mutation loses acknowledgement. Missing-content transfer/backend provisioning remains a gap.");
        Pair("local", "dispatch-core", "local", "local model", "prepare", "result",
            "Selected model binding, prepare/count/generate/cancel and managed load control (in-process today).",
            "Preparation/count, available status, public fragments/usage/terminal outcome and confirmed/uncertain cleanup to host/engine/UI.",
            "Configured accepted-text routing / ModelRuntime / LlamaResourceModelAdapter; one-use prepared generation.",
            "src/Cerebral.Worker/WorkerModelAdapters.cs:34-48; src/Cerebral.Worker/ConfiguredWorkflowModelCoordinator.cs:237,286; src/Cerebral.Worker/ModelRuntime.Prepared.cs:11; src/Cerebral.Contracts/Models/ModelRuntimeContracts.cs:9-115",
            "workers", "machine", failure:"Configured local text admission remains llama-specific; the image/projector route remains legacy. FIFO lease/slot assignment does not promise request affinity or stable logical context/cache affinity. Separate model-facing Worker and common multiplexed tunnel remain targets.");
        Pair("remote", "dispatch-core", "remote", "remote model", "dispatch", "NDJSON",
            "Authenticated remote identity/binding and prepared count/generate/release/cancel control.",
            "One HTTP generation response/NDJSON stream, status/usage/terminal outcome and preparation release result to host/engine/UI.",
            "Current remote prepared HTTP / NDJSON; proposed common tunnel is not running support.",
            "src/Cerebral.Worker/RemotePreparedInference.cs; src/Cerebral.Worker/OwnerPreparedStreamingHttp.cs:122-147",
            "workers", "stream", failure:"Current output buffering is bounded/coalesced. Some routes are terminal-only. Multiplexed channel flow control/cancellation and missing model-content transfer remain gaps.");
        Pair("hosted", "dispatch-core", "hosted", "provider", "generate", "stream",
            "Proposed declared capability/binding/count proof and bounded generation input.",
            "Proposed public stream/usage/terminal/reconcile outcome through generic provider projection to host/engine.",
            "Private model-provider.v1 proposal; vendor protocol stays in provider package.",
            "docs/COMPONENT-COORDINATION.md; docs/CODING-GUIDANCE.md:10-34",
            "providers", "contract-map", true, "No live adoption/provider calls. Hosted estimates/quotas are distinct from exact local tokenizer/physical capacity. Post-send unknown is reconciled without resubmission.");
        Pair("journal", "admission-core", "journal", "durable effects", "intent", "state",
            "Accepted run/effect intent, expected revision and bound engine/profile/source before dispatch.",
            "Durable state/outcome/receipt to scheduler/recovery and next engine decision throughout execution.",
            "WorkflowStore / WorkflowRuntime durable execution ownership.",
            "src/Cerebral.Engine/Persistence/WorkflowStore.cs:38-65; src/Cerebral.Engine/Workflows/WorkflowRuntime.cs:185,315",
            "framework", "contract-map");
        Pair("context-strategy", "engine", "context", "context strategy", "select", "bounded",
            "Task/history/source references, strategy and admitted optional contributions.",
            "Selected frozen bounded context or fit/compaction/overflow decision.",
            "ContextPlanner / ConversationContext; current transitional composition.",
            "src/Cerebral.Engine/Context/ContextPlanner.cs:26; src/Cerebral.Conversation/ConversationContext.cs:45",
            "reference", "context-budget");
        Pair("private-data", "plugins", "private-store", "owned data", "read/write", "records",
            "Plugin-owned private data read/write/migration request.",
            "Plugin-private record/state; only explicit public projection leaves the plugin.",
            "Private owner contract; framework does not decode/migrate this store.",
            "docs/CODING-GUIDANCE.md:28,38,50", "plugins", "contract-map");
        void Flow(string id, string from, string to, string label, string payload, string source)
        {
            edges.Add(new(id, from, to, "workflow", "workflow", label, payload,
                "Current host/workflow mechanism; logical Core ownership, not a new API.",
                "Source-supported mechanism; no live execution claim",
                "Validate identity/revision/outcome; partial or unknown is preserved, not replayed.",
                source, "workflows", "contract-map"));
        }
        Flow("pipeline.admit", "input-core", "admission-core", "submit",
            "Exact input/request/profile/definition IDs and bounded workflow submission.",
            "src/Cerebral.Client/WorkflowConversationClient.cs:100; src/Cerebral.Engine/Persistence/WorkflowStore.cs:38");
        Flow("pipeline.evaluate", "admission-core", "engine-bridge", "evaluate",
            "Frozen accepted run/state and current revision to selected engine evaluation.",
            "src/Cerebral.Engine/Workflows/WorkflowRuntime.cs:131; src/Cerebral.Engine/Activation/EngineController.cs:232; src/Cerebral.Engine/Workflows/WorkflowSchema.cs:16-20,24-98; src/Cerebral.Engine/Workflows/WorkflowRegistry.cs:22-74,94-118,171-179; src/Cerebral.Worker/WorkerWorkflowConversation.cs:64-86; tests/Cerebral.Worker.Tests/WorkflowConversationEffectsTests.cs:61");
        Flow("pipeline.effect", "engine-bridge", "dispatch-core", "effect request",
            "Engine transition selects work; host admits scoped tool/context/model/workflow effect before execution.",
            "src/Cerebral.Engine/Workflows/WorkflowRuntime.cs:185; src/Cerebral.Tools/ToolDispatcher.cs:173");
        Flow("pipeline.result", "dispatch-core", "outcomes-core", "outcome",
            "Returned result/usage/finish/receipt is classified and validated.",
            "src/Cerebral.Worker/WorkerWorkflowEffects.cs:238; src/Cerebral.Engine/Workflows/WorkflowRuntime.cs:315");
        Flow("pipeline.next", "outcomes-core", "engine-bridge", "known → next",
            "Known result/receipt feeds the next engine/context decision; waits/questions/completion remain explicit.",
            "src/Cerebral.Engine/Workflows/WorkflowRuntime.cs:49-74,315");
        Flow("pipeline.publish", "outcomes-core", "publish-core", "public only",
            "Eligible public result/presentation snapshot enters before-commit response hooks; not every private tool result is published.",
            "src/Cerebral.Contracts/Routing/ResponseOutputContracts.cs:47; src/Cerebral.Worker/ServicePluginRuntime.cs:218");
        Flow("pipeline.events", "publish-core", "input-core", "committed events",
            "Authoritative public content/status/projection returns through the client event boundary to Chat.",
            "src/Cerebral.Worker/Program.cs:228,307; src/Cerebral.Worker/LocalWorkerPipeServer.cs:211");
        Pair("outcome-journal", "outcomes-core", "journal", "known / held outcome", "hold / record", "receipt",
            "Known outcomes are recorded; unknown/held effects remain journaled for reconciliation, without replay.", "Durable confirmed revision/receipt or reconciliation hold.",
            "WorkflowStore / WorkflowRuntime ongoing effect journal.",
            "src/Cerebral.Engine/Workflows/WorkflowRuntime.cs:185,315", "workflows", "contract-map");
        Pair("discovery", "dispatch-core", "discovery-core", "discovery", "describe", "schema",
            "Permitted catalog/skill/contract request and selected exact names/topics.",
            "Bounded descriptors, exact input schema, prerequisites and selected guidance.",
            "cerebral.host:catalog v1; skills.list/read; structured sdk.discover/describe v1; eligible plugin.contract API 1.",
            "src/Cerebral.Worker/WorkerWorkflowConversation.cs:102,374; src/Cerebral.Worker/SkillToolExtension.cs:9",
            "reference", "discovery");
        foreach (var worker in new[] { "local", "remote" })
        {
            Pair(worker + "-adapter", worker + "-adapter", worker + "-model", "backend", "prepare", "tokens",
                "Backend roles/templates/content/options and admitted model asset.",
                "Model-specific preparation/count/capability outcome.",
                "Current configured local text uses registered preparation with llama-specific admission; legacy image/projector and remote prepared routes remain distinct. Arbitrary backend adoption is not established.",
                "src/Cerebral.Worker/WorkerModelAdapters.cs:34-48; src/Cerebral.Worker/ConfiguredWorkflowModelCoordinator.cs:286; src/Cerebral.Worker/LlamaResourceModelAdapter.cs; src/Cerebral.Worker/ModelRuntime.Prepared.cs:11",
                "workers", "adapter");
            Pair(worker + "-slot", worker + "-model", worker + "-slot", "capacity", "acquire", "release",
                "Model/instance identity and admitted one-use inference preparation.",
                "Generated outcome and slot release or retained uncertain ownership.",
                "ModelRuntime slot pool / prepared generation lifecycle.",
                "src/Cerebral.Worker/ModelRuntime.cs:30-50; src/Cerebral.Worker/LlamaPreparedGeneration.cs",
                "workers", "instance");
        }
        // Retain the reference ID so saved map selections and exact Help links remain valid.
        edges.Add(new("settings.proposal", "config", "settings-core", "settings UI", "shared persistence",
            "settings", "PluginSettingsCoordinate/global-or-agent documents with package/settings versions and ExpectedRevision; renderer layout and named presets; portable backup 14 or explicitly selected complete-nonsecret backup 15, with readable legacy formats.",
            "SettingsPreferenceContract schema 1; PluginSettingsSaveRequest → PluginSettingsSaveResult(State, Changed). settings.plugins.get/save, interface.layout.get/save, configuration.presets.*; plugin-owned pure admission/projection policy.",
            "Implemented in source; retained private owner evidence. Earlier Core 414 assertions include 29 actual private Worker/pipe/SQLite; Configurator reports 20 shown/private-pipe checks, 3 injected negatives and 64 fake/offscreen checks. These historical tiers are not added or rerun here and do not report current host/Source adoption.",
            "ExpectedRevision CAS and owner validation distinguish rejection from unknown commit/lost reply; reconcile uncertainty without replay. Preserve opaque values/pending documents and settings for missing plugins. Full docking/floating and unmigrated legacy packages remain pending.",
            "src/Cerebral.Core/SettingsPreferenceContracts.cs:7-87; src/Cerebral.Worker/CommandDispatcher.cs:457,496; src/Cerebral.Integrator/IntegratorSettingsLayoutCodec.cs:9; src/Cerebral.Core/CompleteSettingsContracts.cs:7-41; build/Core/SettingsPresets/handoff.json; build/Configurator/SettingsPresets/handoff.json", "getting-started", "configure", false));
        return edges.AsReadOnly();
    }

    static HelpArchitectureCatalog()
    {
        if (Nodes.Count > 32 || Edges.Count > 64 ||
            Nodes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != Nodes.Count ||
            Edges.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != Edges.Count)
            throw new InvalidOperationException("Architecture identity/bounds failed.");
        foreach (var node in Nodes)
        {
            CheckReference(node.PageId, node.SectionId);
            if (node.ParentId is not null && !Nodes.Any(n => n.Id == node.ParentId))
                throw new InvalidOperationException("Architecture parent is absent.");
        }
        foreach (var edge in Edges)
        {
            if (!Nodes.Any(n => n.Id == edge.From) || !Nodes.Any(n => n.Id == edge.To))
                throw new InvalidOperationException("Architecture endpoint is absent.");
            CheckReference(edge.PageId, edge.SectionId);
            foreach (var value in new[] { edge.Id, edge.FromPort, edge.ToPort, edge.Label, edge.Payload, edge.Contract, edge.Status, edge.FailureCancellation, edge.Source })
                if (string.IsNullOrWhiteSpace(value) || value.Length > HelpCatalog.MaximumTextCharacters)
                    throw new InvalidOperationException("Architecture text bound failed.");
        }
    }

    private static void CheckReference(string page, string section)
    { if (!HelpCatalog.GetPage(page).Sections.Any(s => s.Id == section)) throw new InvalidOperationException("Architecture Help reference is absent."); }

    private static HelpArchitectureNode N(string id, string title, string caption, string owner,
        string current, string target, string source, string page, string section, string side, int row, string? parent = null) =>
        new(id, title, caption, owner, current, target, source, page, section, side, row, parent);

    private static HelpArchitectureNode WorkerPart(string id, string title, string caption, string parent, int row, string section) =>
        N(id, title, caption, "Worker / backend runtime",
            "Current source model-specific runtime/slot boundary; no live instance/capacity is claimed.",
            "Adapter/model/instance/slot identities and supported capability remain explicit per machine.",
            "src/Cerebral.Worker/ModelRuntime.cs:30-50; src/Cerebral.Worker/LlamaResourceModelAdapter.cs",
            "workers", section, "inside", row, parent);
}
