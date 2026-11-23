using System.Collections.ObjectModel;

namespace Cerebral.HelpPlugin;

// Keep existing numeric values stable for retained content exports.
public enum HelpSectionKind { Placeholder = 0, TutorialStep = 1, Screenshot = 2, Reference = 3 }
public sealed record HelpSection(string Id, string Title, string Text, HelpSectionKind Kind = HelpSectionKind.Placeholder);
public sealed record HelpPage(string Id, string Title, string Summary, IReadOnlyList<HelpSection> Sections);
public sealed record HelpDiagramNode(string PageId, string Title, string Caption);
/// <summary>Local content metadata only. This is not a registered agent template or executable profile.</summary>
public sealed record HelpAssistantSlot(string Id, string Title, string ConnectionState, bool ProfileRegistered);

/// <summary>
/// Immutable package content, separate from presentation. No file, URL, provider or host lookup
/// occurs while browsing. Bounds keep authored references and deferred tutorial slots small.
/// </summary>
public static class HelpCatalog
{
    public const string ContentVersion = "2026-10-26";
    public const int MaximumPages = 32;
    public const int MaximumSectionsPerPage = 8;
    public const int MaximumTextCharacters = 2000;
    public static HelpAssistantSlot Assistant { get; } = new("pegasus.help:assistant-placeholder", "Help assistant", "pending", false);
    public static IReadOnlyList<HelpDiagramNode> Diagram { get; } = ReadOnly(
        new HelpDiagramNode("framework", "Framework", "Identity, grants, lifecycle and durable effects"),
        new HelpDiagramNode("engine", "Selected engine", "Strategy, context selection and next decisions"),
        new HelpDiagramNode("workers", "Workers", "Machines, adapters, instances and streams"),
        new HelpDiagramNode("plugins", "Plugins", "Optional domain capabilities and public hooks"),
        new HelpDiagramNode("workflows", "Workflows", "Accepted steps, effects and known outcomes"),
        new HelpDiagramNode("providers", "Model providers", "Declared model capabilities and preparation"),
        new HelpDiagramNode("cortex", "Cortex", "Passive references and bounded continuity"));

    public static IReadOnlyList<HelpPage> Pages { get; } = ReadOnly(
        Page("overview", "Overview", "Explore the core pipeline, contract branches and plugin hooks. Tutorials, images and assistant integration are pending.",
            Reference("map", "Workflow reference", "Start with Core and its immediate branches. Explore Core to follow intake → admission → engine/context evaluation → admitted effects → known outcome → public output. Select a component or arrow for its owner, input/output, contract, source and failure/cancellation rules. Explore Plugins or a Worker for their internal connections. This authored source map does not report live status.")),
        Page("getting-started", "Getting started", "Basic orientation is available. The product walkthrough and reviewed images remain pending.",
            Reference("orientation", "Choose a reference", "Use Overview for the core pipeline and component boundaries. Reference explains discovery, contracts and recovery. The map's Help action opens the matching page, section and contract point; Overview returns to the selected map branch. Configuration and model changes belong to their normal reviewed product controls."),
            Step("start", "1. Open Cerebral", "Guide text and a reviewed image will be added here."),
            Step("configure", "2. Choose your setup", "Configuration walkthrough pending. Existing configuration contract points are listed below; their presence does not prove current installation or activation."),
            Step("first-conversation", "3. Start a conversation", "The first-conversation guide is pending.")),
        Page("tutorials", "Tutorials", "Tutorial step slots are reserved for short, approved walkthroughs.",
            Step("tutorial-one", "Tutorial 1 · first task", "Purpose, prerequisites and steps are pending."),
            Step("tutorial-two", "Tutorial 2 · references", "Purpose, prerequisites and steps are pending."),
            Step("tutorial-three", "Tutorial 3 · review a result", "Purpose, prerequisites and steps are pending.")),
        Page("build-plugin", "Build a plugin", "Contract orientation is available. The end-to-end author walkthrough remains pending.",
            Reference("supported-boundary", "Start with the supported contract", "Read the exact selected SDK descriptor and input schema before implementing a contribution. Desktop views use the declared native mount and host lifecycle; service tools/context/output hooks use scoped host calls. Plugins own private data and migrations. The host owns admission, grants and durable effects. The contract references below do not install, enable or execute a plugin."),
            Step("plugin-contract", "1. Select a supported contribution", "A reviewed minimal plugin example is pending."),
            Step("plugin-build", "2. Build a private candidate", "The authored build walkthrough is pending."),
            Step("plugin-review", "3. Review and enable", "The authored package review, verification and enablement walkthrough is pending.")),
        Page("reference", "Reference", "Authored terms, discovery, contract and recovery references. Availability must be verified at the relevant runtime.",
            Reference("context-budget", "Context and token budget", "Keep readable compact handoffs and retrieve exact selected references. Joined words are not single tokenizer tokens. Actual preparation counts determine context fit; a character count or valid JSON does not prove token savings or semantic fidelity. Context/cache affinity and physical model capacity require separate owner evidence."),
            Reference("terms", "Terms and concepts", "Core owns generic admission, lifecycle and effects. The selected engine chooses strategy and context. Plugins supply optional domain behavior through declared hooks. A Worker owns model execution on its machine; an adapter handles backend-specific preparation, a model instance identifies resident resources, and an inference slot represents owned capacity. None of these counts establishes a count of available agents."),
            Reference("discovery", "Agent and capability reference map", "Existing role, tool, skill and SDK-contract discovery ends are mapped below. Select a permitted summary, then read the exact ID/schema or enabled skill body needed for the task. Omitted, filtered or truncated results do not prove a capability is uninstalled. Help is passive and does not register or execute these routes."),
            Reference("contracts", "Plugin and workflow references", "A useful connection identifies caller/provider, version, accepted identity, input, output, cancellation and failure semantics. Map arrows expose these fields with exact Help/source references. Tool catalog input schemas, SDK descriptors, workflow descriptor admission and native model-response schemas are different boundaries. Unsupported workflow schema keywords reject registration. Capability discovery does not grant authority."),
            Reference("recovery", "Diagnostics and recovery", "Keep rejected, unavailable, partial, cancelled and unknown outcomes distinct. Cancellation intent is not confirmed completion or resource release. After dispatch, preserve the accepted identities, revision and effect receipt; inspect or reconcile an unknown effect before retrying. A retained fixture or source reference is evidence for its stated scope, not proof of a running installation.")),
        Page("common-issues", "Common issues", "Reasons, blocking conditions and supported recovery checks. Guidance is authored; current availability must be verified.",
            Reference("catalog", "Common issues and recovery", "Choose one issue below. Read the blocking condition before changing anything. Supported owner actions require the normal owner route and admission. Pending support is labeled explicitly. Help does not execute recovery actions or change accepted work.")),
        Page("images", "Images", "This package contains reserved image slots; reviewed screenshots remain pending.",
            Image("main-window", "Main window screenshot", "A reviewed main-window image and descriptive caption will be added here."),
            Image("settings", "Configuration screenshot", "A reviewed configuration image and descriptive caption will be added here.")),
        Page("assistant", "Help assistant", "Assistant connection pending. No agent profile, conversation or provider is connected."),
        Component("framework", "Framework",
            "The generic host admits work, checks identity/grants, owns durable execution and publishes authoritative public events. Core is a logical library boundary; current composition still resides in Worker.",
            "Chat enters through authenticated command/events. The selected engine returns strategy decisions; the host dispatches admitted effects to plugins, external tools and model execution, then records outcomes and publishes eligible output."),
        Component("engine", "Selected engine",
            "The selected versioned engine chooses strategy, next actions and bounded context. The host owns generic admitted execution and accepted bindings.",
            "EngineRequest carries bound input/state/revision. EngineDecision returns transition/output/effect requests. Known results feed the next evaluation; uncertain effects remain held for reconciliation."),
        Page("workers", "Workers", "Machine, adapter, model and stream references are available. Runtime availability and capacity are separate checks.",
            Reference("machine", "Machine", "The intended model-facing Worker owns several adapters and resident models on one machine. Current Worker still combines host/domain/model facilities. Accepted local text can use configured registered preparation; the image/projector route remains separate. Backend support, installed model content and current hardware limits must be established independently."),
            Reference("adapter", "Model adapter", "The adapter translates a declared model contract into backend-specific roles/templates, preparation, token counts and generation. Current configured local text admission still requires worker.local.llama.cpp; registered routing does not establish arbitrary backend support. Preparation and native response-schema validation belong to this model boundary."),
            Reference("instance", "Model instance and inference slot", "A resident model binds exact asset/model/instance identities. A prepared inference owns one-use capacity and cleanup. FIFO pool assignment is not stable request-to-slot or logical-context/cache affinity. Do not infer available agents, physical KV capacity or installation-wide maxima from a fixture or one hardware case."),
            Reference("stream", "Stream and terminal outcome", "Current remote prepared generation uses authenticated HTTP with an NDJSON response; a common correlated multiplexed tunnel is target work. Keep fragments, usage and terminal outcome distinct. Missing finish/error/token/cancellation facts remain unavailable, and resource closure does not resolve an unknown execution outcome.")),
        Component("plugins", "Plugins",
            "Optional packages own domain behavior, private stores, settings meaning and migrations. Core owns generic lifecycle/storage and admission; shell layout remains separate from plugin content.",
            "Tools, context contributions, response hooks and native mounts connect through declared contracts. Context policy remains package-owned; after-commit observers cannot rewrite authoritative events. Plugins have no direct model bypass."),
        Component("workflows", "Workflows",
            "Versioned workflows bind exact request/profile/definition and accepted input before effects run. The host journals intent and known or held outcomes.",
            "Follow intake/admission, engine evaluation, admitted tool/context/model effect and classified result. A known result may continue the engine loop or publish eligible public output; unknown effects require reconciliation rather than replay."),
        Component("providers", "Model providers",
            "Providers own backend-specific preparation and model capability details. The host consumes declared bindings, results and ownership outcomes.",
            "Local/native and remote prepared model routes have separate evidence from the private hosted-provider proposal. Hosted estimates/quotas are not exact native token counts or physical capacity. Live provider adoption is not inferred from the proposal."),
        Component("cortex", "Cortex",
            "Cortex owns separate continuity/retrieval and asset stores. Retained material remains passive reference data.",
            "Bounded search previews select exact typed reads. Authorized checkpoints use the exact task/namespace binding. Missing Cortex follows explicit permitted degradation; no component opens another product's database as a shortcut."));

    static HelpCatalog()
    {
        if (Pages.Count > MaximumPages || Pages.Select(page => page.Id).Distinct(StringComparer.Ordinal).Count() != Pages.Count)
            throw new InvalidOperationException("Help catalog page bounds or identities are invalid.");
        foreach (var page in Pages)
        {
            CheckText(page.Id); CheckText(page.Title); CheckText(page.Summary);
            if (page.Sections.Count > MaximumSectionsPerPage ||
                page.Sections.Select(section => section.Id).Distinct(StringComparer.Ordinal).Count() != page.Sections.Count)
                throw new InvalidOperationException("Help section bounds or identities are invalid.");
            foreach (var section in page.Sections)
            { CheckText(section.Id); CheckText(section.Title); CheckText(section.Text); }
        }
        foreach (var node in Diagram)
        {
            CheckText(node.Title); CheckText(node.Caption);
            if (!Pages.Any(page => page.Id == node.PageId)) throw new InvalidOperationException("Help diagram target is absent.");
        }
    }

    public static HelpPage GetPage(string id) => Pages.FirstOrDefault(page => page.Id == id)
        ?? throw new ArgumentException("Unknown Help page.", nameof(id));

    private static HelpPage Component(string id, string title, string purpose, string connections) => Page(id, title,
        "Authored component and contract references. Reviewed images remain pending.",
        Reference("contract-map", "Established contract points", "The map provides a bounded inventory of source symbols, entry/exit points, identities and failure/cancellation semantics. These authored references describe source and intended boundaries; they do not add an API or prove running integration."),
        Reference("purpose", "Purpose and responsibilities", purpose),
        Reference("connections", "Inputs, outputs and connections", connections),
        Image("component-image", "Component screenshot or illustration", "A reviewed image and descriptive caption will be added here."));
    private static HelpPage Page(string id, string title, string summary, params HelpSection[] sections) =>
        new(id, title, summary, ReadOnly(sections));
    private static HelpSection Reference(string id, string title, string text) => new(id, title, text, HelpSectionKind.Reference);
    private static HelpSection Step(string id, string title, string text) => new(id, title, text, HelpSectionKind.TutorialStep);
    private static HelpSection Image(string id, string title, string text) => new(id, title, text, HelpSectionKind.Screenshot);
    private static ReadOnlyCollection<T> ReadOnly<T>(params T[] items) => Array.AsReadOnly(items);
    private static void CheckText(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumTextCharacters)
            throw new InvalidOperationException("Help text is empty or exceeds its bound.");
    }
}
