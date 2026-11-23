namespace Cerebral.HelpPlugin;

/// <summary>Authored reference data, never a live inventory or executable host contract.</summary>
public sealed record HelpFlowStage(
    string Id, string Title, string Caption, string Owner, string Input, string Output,
    string Current, string Target, string Contract, string Entry, string Exit,
    string FailureCancellation, string Source, string PageId, string SectionId,
    bool Optional = false, IReadOnlyList<string>? ConnectsTo = null);

public static class HelpWorkflowCatalog
{
    public const string EvidenceLabel = "Source map · intended boundaries · no live status";
    public static IReadOnlyList<HelpFlowStage> Stages { get; } = Array.AsReadOnly(new[]
    {
        new HelpFlowStage("request", "Request", "Chat and task input", "Chat shell / selected engine",
            "User message, attachments and selected task.", "A scoped request for context and strategy.",
            "The host has working built-in conversation and client submission paths.",
            "The shell remains usable without an engine; the selected engine supplies conversation policy.",
            "pegasus.cerebral.desktop/v1: IDesktopPluginHost.SubmitAsync; authenticated Worker command dispatch.",
            "IDesktopPluginHost.SubmitAsync; LocalWorkerPipeServer dispatch.", "Accepted input or explicit rejection.",
            "Submission cancellation is passed explicitly; rejection does not imply admitted effects.",
            "src/Cerebral.Desktop.Plugins/DesktopPluginContract.cs:190-197; src/Cerebral.Worker/LocalWorkerPipeServer.cs; docs/CODING-GUIDANCE.md:10-18",
            "framework", "contract-map"),
        new HelpFlowStage("context", "Context", "Task, history, references", "Engine / context contributors",
            "Current task, history and admitted references; optional style contributions.", "Bounded context for the selected strategy.",
            "Conversation/context implementation still exists in the transitional host.",
            "Engine/context pipeline combines task, history and optional plugin-owned personality.",
            "Existing context/harness boundaries; optional contributions retain owner identity.",
            "Conversation preparation and selected context contributions.", "Prepared context or an actionable unavailable state.",
            "Bounds and cancellation apply; unavailable optional context must be explicit.",
            "src/Cerebral.Engine/Context/ContextPlanner.cs:26; src/Cerebral.Conversation/ConversationContext.cs:45; docs/CODING-GUIDANCE.md:18-28",
            "reference", "context-budget"),
        new HelpFlowStage("engine", "Selected engine", "Conversation strategy", "Engine owner",
            "Scoped request and bounded context.", "Strategy, work selection and requests for declared capabilities.",
            "Replaceable engines exist; host still owns some conversation/model machinery.",
            "Conversation policy, task coordination and domain workflows belong to engine/plugins.",
            "IEnginePlugin.Descriptor / EvaluateAsync; pegasus.cerebral.engine protocol 1; selected immutable engine/profile binding.",
            "IEnginePlugin.EvaluateAsync receives a host-issued versioned strategy request.", "Strategy/work requests cross explicit host adapters.",
            "Accepted work retains its frozen binding; unsupported capabilities are diagnosed.",
            "src/Cerebral.Engine.Contracts; docs/CODING-GUIDANCE.md:10-34",
            "engine", "contract-map"),
        new HelpFlowStage("admission", "Admitted work", "Scope and durable effects", "Framework / domain effect owner",
            "Engine-selected capability and scoped input.", "Admitted execution, rejection or retained uncertain outcome.",
            "The host owns grants, revisions, workflow/effect state and resource ownership.",
            "Domain behavior remains in packages; generic lifecycle and durable mechanics remain in framework.",
            "Existing command/event and workflow/effect contracts; no generic bypass.",
            "Capability schema and version are resolved before admitted input.", "Typed outcome or durable reference.",
            "Cancellation preserves known state; uncertain effects require reconciliation before retry.",
            "docs/CODING-GUIDANCE.md:36-48,70-78; src/Cerebral.Core",
            "workflows", "contract-map"),
        new HelpFlowStage("worker", "Worker", "One machine's model host", "Worker / runtime owner",
            "Model binding, supported operation and scoped generation input.", "Preparation, count, stream, cancellation and cleanup outcomes.",
            "Source supports multiple resident llama.cpp instances. Worker still composes coordinator/domain facilities.",
            "One model-facing Worker per machine; several adapters/resident models over one correlated multiplexed tunnel.",
            "Current local pipe and remote prepared/NDJSON routes; multiplexing is a target.",
            "LocalWorkerPipeServer; RemotePreparedInference.", "Per-request response/stream and owned preparation release.",
            "One local request per connection today; one generation per remote HTTP response. Independent channel cancellation is target work.",
            "src/Cerebral.Worker/LocalWorkerPipeServer.cs:243-245; src/Cerebral.Worker/Program.cs:155-171; docs/CODING-GUIDANCE.md:20-26",
            "workers", "machine"),
        new HelpFlowStage("adapter", "Backend adapter", "Roles, templates, tokens", "Model adapter package / Worker",
            "Common request plus model/adapter identity.", "Backend-specific preparation, counts and generation parameters.",
            "The Worker backend is hardcoded to llama.cpp; a replaceable backend registry is absent.",
            "Adapters translate templates, roles, tokenization, content kinds and outcomes; ordinary models are asset/adapter additions.",
            "Current LlamaResourceModelAdapter; proposed multi-backend seam is not an available API.",
            "Model-specific preparation/counting.", "Prepared backend request and explicit supported capabilities.",
            "Text context size does not prove image/audio/tool/schema/reasoning/cancellation capability.",
            "src/Cerebral.Worker/LlamaResourceModelAdapter.cs; docs/CODING-GUIDANCE.md:22-26",
            "workers", "adapter"),
        new HelpFlowStage("model", "Resident model", "Instances and inference slots", "Worker / backend runtime",
            "Admitted model asset, preparation and resource/slot binding.", "Generated content and measured runtime outcomes.",
            "ModelRuntime owns multiple llama instances and slot/resource lifecycle.",
            "Several replaceable backends and resident instances share one Worker control host.",
            "ModelRuntime / prepared generation; model, instance and slot identities differ.",
            "Model load and one-use preparation.", "Generation outcome and confirmed cleanup or retained uncertainty.",
            "Cancellation must preserve uncertain native ownership until actual cleanup is known.",
            "src/Cerebral.Worker/ModelRuntime.cs; src/Cerebral.Worker/LlamaPreparedGeneration.cs; docs/CODING-GUIDANCE.md:20-26",
            "workers", "instance"),
        new HelpFlowStage("stream", "Stream", "Content and terminal outcome", "Worker transport / client",
            "Request-correlated generation events.", "Ordered content, completion, cancellation or failure.",
            "Local pipe is one request per connection; remote NDJSON carries one generation per HTTP response.",
            "Negotiated channels/request sequencing, bounded queues and independent cancellation on one multiplexed tunnel.",
            "Existing pipe/remote NDJSON; Cortex native/2 does not prove Cerebral multiplexing.",
            "Current generation route.", "Client consumes stream and terminal outcome.",
            "A partial stream is not completion. Current remote buffering is bounded; multiplexed per-channel backpressure/cancellation remains target work. Some routes are terminal-only.",
            "src/Cerebral.Worker/LocalWorkerPipeServer.cs:243-245; src/Cerebral.Worker/OwnerPreparedStreamingHttp.cs:122-147; src/Cerebral.Worker/RemotePreparedStreamBuffer.cs:37-52; docs/CODING-GUIDANCE.md:20",
            "workers", "stream"),
        new HelpFlowStage("result", "Result", "Review and continue", "Engine / Chat / domain package",
            "Content, terminal outcome and artifact references.", "User-visible result and next engine decision.",
            "Host conversation/result rendering and domain outcomes are source-supported.",
            "Engine decides follow-up strategy; domain packages interpret their own results.",
            "Existing conversation presentation and typed effect outcomes.",
            "Completed/partial/rejected/uncertain outcome arrives.", "Displayed result or next scoped work.",
            "A build pass is not runtime acceptance; uncertain work is not blindly replayed.",
            "docs/CODING-GUIDANCE.md:70-78; src/Cerebral.Orchestrator",
            "reference", "recovery", false, Array.AsReadOnly(new[] { "engine" })),
        new HelpFlowStage("persistence", "Persistence", "Durable state from admission", "Framework / each data owner",
            "Accepted conversation/effect state and package-owned changes.", "Durable state and exact artifact/reference identities.",
            "Host owns durable execution; plugins own private data, schemas and migrations.",
            "Replace code without losing chats/artifacts; Cortex remains a separate continuity/reference store.",
            "Owner-scoped stores and public projections; no cross-product database access.",
            "Owning component commits its state.", "Recoverable accepted state or an actionable failure.",
            "Preserve revisions and accepted bindings; failed candidates retain previous selection/data.",
            "src/Cerebral.Engine/Persistence/WorkflowStore.cs:38-65; docs/CODING-GUIDANCE.md:28,46-68,80-96",
            "framework", "contract-map", false, Array.AsReadOnly(new[] { "admission" })),
        new HelpFlowStage("cortex", "Cortex references", "Optional → Context / continuity", "Cortex coordinator and data owners",
            "Bounded query in explicit spaces.", "Selected exact typed records/revisions or asset references.",
            "Cerebral uses the supported native pipe; Codex uses installed MCP.",
            "Optional continuity/retrieval improves context while preserving product ownership.",
            "Installed Cortex schema/guidance, exact task and namespace; no Help connection.",
            "Search previews; exact typed read selected coordinates.", "Passive content/reference; owning task checkpoints separately.",
            "Unavailable references are explicit. Retained content cannot authorize effects.",
            "docs/CODING-GUIDANCE.md:80-96", "cortex", "contract-map", true, Array.AsReadOnly(new[] { "context", "persistence" })),
        new HelpFlowStage("plugins", "Plugins", "Optional → Engine / admitted work", "Plugin owner",
            "Declared contribution and validated scoped input.", "Capability, UI/context contribution or typed domain outcome.",
            "SDK contribution/mount interfaces exist; Help uses sidebar.tabs.",
            "Packages own domain data/migrations; shell owns generic adjustable/docked/floating layout.",
            "pegasus.cerebral.desktop/v1: DesktopMountView / IDesktopPluginHost; pegasus.cerebral.service/v1: IServicePlugin / IServiceCommandExtension.",
            "Discover package identity/version and contribution.", "Host consumes declared public projection.",
            "Missing optional packages preserve shell and unrelated capabilities; disposal ends owned subscriptions.",
            "src/Cerebral.Desktop.Plugins; examples/Cerebral.HelpPlugin/HelpPlugin.cs; docs/CODING-GUIDANCE.md:28-30",
            "plugins", "contract-map", true, Array.AsReadOnly(new[] { "engine", "admission" })),
        new HelpFlowStage("configure", "Configuration", "Setup → Worker / model", "Configurator / installation / runtime owners",
            "Reviewed backend/adapter package, model asset and target machine.", "Staged configuration and verified availability.",
            "Model distribution refuses missing exact content; actual missing-content transfer is absent.",
            "One existing provisioning workflow for local/remote Workers; destination-specific binaries with architecture-neutral semantics.",
            "service-packages.stage v1: manifestPath, expectedId, expectedRevision → reviewed identity/revision or NotSubmitted/Unknown. Model assets: pegasus.cerebral.model v1/v2 GGUF.",
            "Configuration selects reviewed package/asset and destination.", "Stage, validate and configure; actual runtime proof is separate.",
            "Failed candidate preserves existing state. Cross-compilation is not remote hardware acceptance.",
            "src/Cerebral.Integrator/IntegratorServicePackages.cs:53; src/Cerebral.Worker/ModelDistribution.cs:95,131; docs/CODING-GUIDANCE.md:24-26",
            "getting-started", "configure", true, Array.AsReadOnly(new[] { "worker", "model" })),
        new HelpFlowStage("capability", "Build a capability", "Build → Plugins / configuration", "Programming / package / framework owners",
            "Requested capability, supported SDK facet and admitted workflow.", "Private candidate, verification evidence and staged package.",
            "Build/verify/stage mechanics exist; a candidate or fixture is not accepted live behavior.",
            "AI callers reuse the same admitted provisioning/build workflow and verify before use.",
            "Existing programming/workflow and package admission contracts; no Help execution path.",
            "Select supported contribution; admitted build/verify steps.", "Reviewed stage/configure/use through existing owner boundaries.",
            "Retain exact failures; do not weaken checks or activate from a diagram.",
            "docs/CODING-GUIDANCE.md:24-34,70-78; examples/Cerebral.ProgrammingPlugin",
            "build-plugin", "plugin-contract", true, Array.AsReadOnly(new[] { "plugins", "configure" }))
    });

    static HelpWorkflowCatalog()
    {
        if (Stages.Count > 24 || Stages.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != Stages.Count)
            throw new InvalidOperationException("Workflow stage identities or bounds are invalid.");
        foreach (var stage in Stages)
        {
            var page = HelpCatalog.GetPage(stage.PageId);
            if (!page.Sections.Any(s => s.Id == stage.SectionId))
                throw new InvalidOperationException("Workflow Help section is absent.");
            foreach (var field in new[] { stage.Id, stage.Title, stage.Caption, stage.Owner, stage.Input, stage.Output,
                stage.Current, stage.Target, stage.Contract, stage.Entry, stage.Exit, stage.FailureCancellation, stage.Source })
                if (string.IsNullOrWhiteSpace(field) || field.Length > HelpCatalog.MaximumTextCharacters)
                    throw new InvalidOperationException("Workflow text exceeds its bound.");
            foreach (var target in stage.ConnectsTo ?? [])
                if (!Stages.Any(s => s.Id == target)) throw new InvalidOperationException("Workflow connection is absent.");
        }
    }
}
