using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Tools;

/// <summary>
/// Creates one deterministic, source-owned plugin project from the SDK assemblies supplied by the host.
/// The model edits the generated source and uses the ordinary build/presentation tools; this operation
/// never builds, installs, enables, or executes the generated code.
/// </summary>
public sealed class PluginScaffoldToolExtension : IToolExtension
{
    public const string ProjectsToolName = "plugin.projects";
    public const string ContractToolName = "plugin.contract";
    public const string ToolName = "plugin.scaffold";
    public const string VerifyToolName = "plugin.verify";
    private const string DesktopComponent = "desktop";
    private const string ServiceComponent = "service";
    private const string CrossSurfaceComponent = "cross-surface";
    private readonly string? desktopSdkAssembly;
    private readonly string serviceSdkAssembly;
    private readonly ConcurrentDictionary<string, InventoryAdmission> inventoriedJobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InventoryScanState> pendingInventories = new(StringComparer.Ordinal);
    // Serialize host-owned source admission across extension instances and assignments.
    // The filesystem is read again inside this gate; a previous discovery is never the catalog.
    private static readonly object SourceInventoryGate = new();
    private const int MaximumInventoryDirectories = 2_000;
    private const int MaximumInventoryProjects = 50;
    private const int MaximumInventoryPages = 32;
    private static readonly TimeSpan InventoryContinuationLifetime = TimeSpan.FromMinutes(5);

    public PluginScaffoldToolExtension(string? desktopSdkAssembly, string serviceSdkAssembly)
    {
        this.desktopSdkAssembly = NormalizeOptionalSdk(desktopSdkAssembly, "Desktop");
        this.serviceSdkAssembly = NormalizeSdk(serviceSdkAssembly, "Service");
    }

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(ProjectsToolName,
            "For a NEW plugin, call plugin.projects with {} (omit root) to discover saved project roots before scaffolding; if a continuation token is returned, repeat with only that token until complete. Partial pages never establish absence or scaffold admission. This first call does not select a project or build. Examining an existing example does not choose it for modification. For a new named plugin when existing plugins must be preserved, use its own absent scaffold target and distinct ID/name after complete, untruncated discovery; unrelated sources remain reference examples only. When a row matches the requested existing plugin, copy its absolute root verbatim and immediately call plugin.projects again with {\"root\":\"<exact returned root>\"} before reading, editing, building, verifying, or presenting it. Never substitute its id, name, basename, component directory, manifest, or a guessed path. Only that exact-root response supplies the authoritative build envelope. The optional root accepts ONLY an existing generated plugin directory containing PLUGIN.md; do not pass its parent Projects folder or a proposed new directory. Scaffold only when no suitable source matches a complete untruncated discovery: either legacy limited=false, or a full identitySummary with identityScaffoldReady=true even when detailed rows are limited. A partial or truncated page never establishes absence. For an existing authoring target, targetPath (mutually exclusive with root) resolves only that scoped path to its exact SDK source root/component and current inspection; a targetMatched=false receipt means no SDK project was found within the bounded admitted ancestors and grants no authority or scaffold admission. This tool does not build, install, enable, or execute code.",
            CerebralJson.Element(new
            {
                type = "object",
                properties = new
                {
                    root = new { type = "string", minLength = 1, maxLength = 32768,
                        description = "Exact existing generated plugin root returned by discovery, containing PLUGIN.md. Never pass a parent Projects folder or a proposed new directory." },
                    targetPath = new { type = "string", minLength = 1, maxLength = 32768,
                        description = "Existing scoped source or authoring path to inspect for its generated plugin root; mutually exclusive with root. A non-match grants no scaffold admission." },
                    continuation = new { type = "string", minLength = 32, maxLength = 32,
                        description = "Opaque token returned by an incomplete plugin.projects {} discovery. Use alone to continue that same job's inventory; neither this nor a partial page grants scaffold admission." }
                },
                maxProperties = 1,
                additionalProperties = false
            }), "filesystem.read")
        {
            PrerequisiteTools = [ContractToolName, "file.read", "file.edit", "process.run", "program.source.generate"]
        },
        new(ContractToolName,
            "Research the host's exact desktop SDK for the requested behavior before editing source. Topic desktop-base covers entry/sidebar panels; storage covers remembered values, saved settings and private files; draft covers unsent text; new-conversation covers starter pages; mount-view covers other UI slots; conversation, response-settings, conversation-settings, settings-navigation and composer-command cover those host features. conversation-settings is the optional per-chat Properties editor contract; settings-navigation opens a plugin-owned global Configuration panel through the optional scoped host. Select only needed topics yourself; the user need not name a framework or SDK. Results give bounded public signatures and a compile-ready example where useful. Never substitute a guessed API or unrelated framework.",
            CerebralJson.Element(new
            {
                type = "object",
                properties = new
                {
                    topic = new
                    {
                        type = "string",
                        @enum = new[] { "desktop-base", "mount-view", "new-conversation", "draft", "storage", "conversation", "response-settings", "conversation-settings", "settings-navigation", "composer-command" }
                    }
                },
                required = new[] { "topic" },
                additionalProperties = false
            }), "filesystem.read"),
        new(ToolName,
            "Create one new versioned Pegasus Cerebral plugin source scaffold beneath the saved project roots after complete, untruncated plugin.projects discovery. targetDirectory must be absent: plugin.scaffold creates and publishes it atomically, so do not call file.mkdir for that directory first. A rejected scaffold does not establish a project: do not mkdir its components or invent source/project files; choose an absent target and retry after any required inventory refresh. Preserve the scaffold manifest entryType and exact entry class namespace/name; deliberately changing that type requires updating both source and source manifest, then rebuilding. Immediately before writing it rechecks all reviewed source roots and rejects any existing package ID or display name, including from other chats; reuse its exact source or choose both a distinct ID and name. Choose desktop, service, or cross-surface. Preserve an explicit human-selected name; otherwise use the admitted request's project or feature name and provide its purpose in description when available. This tool preserves the supplied ID/name and has no separate project-purpose context; omitted purpose is not inferred. New project and assembly filenames derive from the stable plugin ID; do not rename an existing package or its data when its display text changes. The tool copies the host's exact public SDK assembly and writes reproducible source, project, manifest, and offline NuGet files. The desktop scaffold is intentionally inert: implement only the requested mounts, actions, commands, or service calls against real contracts. This tool does not build, install, enable, or run code. Edit the generated source, build it through process.run, then queue only the exact built desktop-plugin.json through plugin.present. Service staging and activation remain owner operations.",
            CerebralJson.Element(new
            {
                type = "object",
                properties = new
                {
                    pluginId = new { type = "string", minLength = 1, maxLength = 64, pattern = "^[a-z0-9][a-z0-9._-]{0,63}$" },
                    name = new { type = "string", minLength = 1, maxLength = 100,
                        description = "Preserve an explicit human-selected name. Otherwise use the project or feature name from the admitted request; do not invent project context." },
                    description = new { type = "string", minLength = 1, maxLength = 2000,
                        description = "Supply the specific purpose from the admitted project or feature request when available. Explicit human wording is preserved. When omitted, the fallback only identifies the supplied name/component; it does not infer an unavailable project purpose." },
                    targetDirectory = new { type = "string", minLength = 1, maxLength = 32768 },
                    component = new { type = "string", @enum = new[] { DesktopComponent, ServiceComponent, CrossSurfaceComponent } }
                },
                required = new[] { "pluginId", "name", "targetDirectory", "component" },
                additionalProperties = false
            }), "filesystem.write")
        {
            PrerequisiteTools = [ContractToolName, "file.read", "file.edit", "process.run", "program.source.generate"]
        },
        new(VerifyToolName,
            "Verify all components beneath the generated project root returned by plugin.projects or plugin.scaffold: the directory containing PLUGIN.md and Desktop/Service. Do not pass the Desktop/Service component, bin directory, built manifest, or assembly as root. Before reporting build completion or installation, this tool requires each component's exact Release manifest and DLL, checks package identity agreement, and rejects output older than edited source. It does not build, install, enable, or run code.",
            CerebralJson.Element(new
            {
                type = "object",
                properties = new { root = new { type = "string", minLength = 1, maxLength = 32768 } },
                required = new[] { "root" }, additionalProperties = false
            }), "filesystem.read")
        {
            PrerequisiteTools = [ProjectsToolName, ContractToolName, "file.read", "file.edit", "process.run"]
        }
    ];

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (call.Name == ProjectsToolName) return Task.FromResult(Projects(call, context, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (call.Name == ContractToolName) return Task.FromResult(Contract(call, context));
        if (call.Name == VerifyToolName) return Task.FromResult(Verify(call, context));
        if (call.Name != ToolName) throw new ArgumentException("Unknown plugin SDK tool.");
        try
        {
            ToolPolicy.Grant(context.Profile, ToolName, "filesystem.write");
            RequirePluginAuthor(context.Profile, "Plugin scaffolding");
            if (!inventoriedJobs.ContainsKey(context.JobId))
                throw new InvalidOperationException("Call plugin.projects with {} in this assignment and obtain a complete, untruncated source inventory before scaffolding. Reuse and repair a matching source project instead of creating a duplicate.");

            // Source generation is always confined to the profile's reviewed project roots.
            // Application Full Access is reserved for protected execution/installation and must
            // never turn this native source writer into an unrestricted filesystem operation.
            var projectContext = context with { FullAccess = false };
            var pluginId = ToolPolicy.Text(call.Arguments, "pluginId");
            var name = ToolPolicy.Text(call.Arguments, "name");
            var component = ToolPolicy.Text(call.Arguments, "component");
            var target = ToolPolicy.PathInScope(ToolPolicy.Text(call.Arguments, "targetDirectory"), projectContext);
            ValidateIdentity(pluginId, name, component);
            var description = ReadPurposeDescription(call.Arguments);
            if (component is DesktopComponent or CrossSurfaceComponent && desktopSdkAssembly is null)
                throw new InvalidOperationException("The current Worker has no exact desktop SDK binding. Reconnect through the current Pegasus Cerebral desktop before scaffolding a desktop component.");
            if (File.Exists(target) || Directory.Exists(target))
                throw new IOException("The scaffold target already exists. plugin.scaffold creates and publishes targetDirectory atomically, so do not call file.mkdir for it first. If this is generated source containing PLUGIN.md, reuse it through plugin.projects {\"root\":\"<exact target>\"}. Otherwise choose a different absent package directory and retry; no source was written.");

            var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("The target directory has no parent.");
            _ = ToolPolicy.PathInScope(parent, projectContext);
            lock (SourceInventoryGate)
            {
                ToolPolicy.Grant(context.Profile, ProjectsToolName, "filesystem.read");
                if (!inventoriedJobs.TryGetValue(context.JobId, out var admission))
                    throw new InvalidOperationException("Call plugin.projects again before another scaffold attempt.");
                var inventory = DiscoverProjects(projectContext, cancellationToken);
                if (!inventory.Roots.SequenceEqual(admission.Roots, PathComparer))
                {
                    inventoriedJobs.TryRemove(context.JobId, out _);
                    throw new InvalidOperationException("The reviewed project roots changed. Call plugin.projects with {} again before scaffolding.");
                }
                var conflict = inventory.Projects.SelectMany(project => project.Components.Select(part => new { project.Root, part.Id, part.Name }))
                    .FirstOrDefault(part => string.Equals(part.Id, pluginId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(part.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (conflict is not null)
                    throw new InvalidOperationException($"Plugin identity conflict: id='{conflict.Id}', name='{conflict.Name}', root='{conflict.Root}'. Reuse and repair this exact source with plugin.projects {{\"root\":\"<exact conflict root>\"}}, or choose a different package ID and display name for a distinct plugin. A new chat does not create a separate plugin catalog.");
                if (!inventory.TraversalComplete)
                {
                    inventoriedJobs.TryRemove(context.JobId, out _);
                    throw new InvalidOperationException("Plugin source inventory is incomplete; no new source was written. " +
                        inventory.IncompleteReason + " Repair or narrow the reviewed project roots, call plugin.projects with {} again, and do not infer that a missing identity is available.");
                }

                // Malformed or conflicting requests retain their discovery admission for correction.
                // Only a complete fresh inventory may consume it, before the first source mutation.
                if (!inventoriedJobs.TryRemove(context.JobId, out _))
                    throw new InvalidOperationException("Call plugin.projects again before another scaffold attempt.");
                if (File.Exists(target) || Directory.Exists(target))
                    throw new IOException("The scaffold target appeared after admission; no source was written. Call plugin.projects with {} again, then choose an absent target directory.");
                Directory.CreateDirectory(parent);
                ToolPolicy.RejectLinks(parent);
                var temporary = Path.Combine(parent, ".cerebral-plugin-scaffold-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(temporary);
                    WriteAll(temporary, pluginId, name, component, description, cancellationToken);
                    Directory.Move(temporary, target);
                }
                finally
                {
                    if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
                }
            }

            return Task.FromResult(ToolPolicy.Result(Result(target, pluginId, name, component, description)) with
            {
                Evidence = new(ToolExecutionOutcome.Succeeded, "cerebral.plugin-sdk", State: "scaffolded"),
                RequiredSuccessfulTools = component == ServiceComponent ? [VerifyToolName] : [VerifyToolName, "plugin.present"]
            });
        }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or
                                      IOException or InvalidDataException or InvalidOperationException)
        {
            return Task.FromResult(new ToolResult(false, error.Message)
            {
                Evidence = new(ToolExecutionOutcome.Rejected, "cerebral.plugin-sdk", State: "rejected")
            });
        }
    }

    private static ToolResult Contract(ToolCall call, ToolExecutionContext context)
    {
        try
        {
            ToolPolicy.Grant(context.Profile, ContractToolName, "filesystem.read");
            RequirePluginAuthor(context.Profile, "Plugin SDK contract lookup");
            if (call.Arguments.ValueKind != JsonValueKind.Object ||
                call.Arguments.EnumerateObject().Any(property => property.Name != "topic"))
                throw new ArgumentException("Plugin contract lookup accepts exactly one topic.");
            var topic = ToolPolicy.Text(call.Arguments, "topic");
            var reference = topic switch
            {
                "desktop-base" => new
                {
                    summary = "Prefer DesktopPluginBase: override only requested collections and the view factory. Its inherited Initialize already retains Host and optional Environment; do not override Initialize/Dispose unless managing additional resources. Environment here is the plugin host capability, not System.Environment or a WinRT API. For remembered values or settings, research topic storage before writing their implementation; it supplies Environment.DataDirectory and bounded atomic file helpers. Do not guess an operating-system data location. Every entry must have a public parameterless constructor; the host calls Initialize after construction, with no constructor dependency injection. Use Panels for a requested sidebar; no mount interface is needed. ComposerActions create composer buttons. Keep unrequested collections inherited and empty. A WPF button handler must update the actual controls it displays: changing an unbound backing field alone never refreshes the view. Capture those controls in the view factory's local functions, or use real data binding.",
                    signatures = DesktopBaseContract,
                    example = DesktopBaseExample
                },
                "mount-view" => new
                {
                    summary = "Implement IDesktopMountPlugin for descriptor-first UI. This exact example mounts a live compact Orchestrator status view, declares its Orchestrator applicability, and uses only public API v1 types.",
                    signatures = MountViewContract,
                    example = MountViewExample
                },
                "new-conversation" => new
                {
                    summary = "Implement a conversation.start landing page with ordinary WPF controls. Starter buttons replace the current host-owned draft and never submit it automatically.",
                    signatures = NewConversationContract,
                    example = NewConversationExample
                },
                "conversation" => new
                {
                    summary = "Conversation state is read synchronously from the initialized host and refreshed from host events on the WPF dispatcher. This snapshot has no UnsentText or UnsentDraft member. For unsent text use topic draft and host.GetDraft().Text; for a private data path use topic storage.",
                    signatures = ConversationContract,
                    example = ConversationExample
                },
                "draft" => new
                {
                    summary = "Read the current unsent draft synchronously from the initialized host. This complete read-only sidebar example captures its own TextBlock in the Refresh handler; do not search or depend on the application's private visual tree. Adapt its display to the requested behavior. No input hook, command, service, or submission capability is needed.",
                    signatures = DraftContract,
                    example = DraftExample
                },
                "storage" => new
                {
                    summary = "When deriving DesktopPluginBase, use its initialized Environment.DataDirectory in the view factory for private mutable files; no Initialize override is needed. Direct IDesktopPlugin implementations may cast pluginHost to IDesktopPluginEnvironment in Initialize. Do not inject it through a constructor. PackageDirectory is replaceable code, not data. DesktopPluginFiles already implements bounded strict UTF-8 reads and atomic saves, including directory creation; use these methods instead of wrappers or duplicate file I/O. Limits count UTF-8 bytes, not string.Length. Create the actual view controls before loading. Reload updates that same control. Changing a backing field alone does not refresh WPF. " + ToolModelProjection.DesktopPluginStorageGuidance,
                    signatures = StorageContract,
                    example = StorageExample
                },
                "response-settings" => new
                {
                    summary = "Response allocation is an optional host interface. RequestedSharePercent is nullable and EffectiveResponseTokens is the host-computed output budget.",
                    signatures = ResponseSettingsContract,
                    example = ResponseSettingsExample
                },
                "conversation-settings" => new
                {
                    summary = "Optional version-1 per-chat Properties editors. The host supplies the exact saved target, which may differ from Host.GetConversation(). Each opening returns a fresh unparented editor View with explicit SaveAsync, Cancel and Dispose. Opening/cancelling does not save. Plugin storage and concurrency remain plugin-owned, independently of core response settings; the host does not promise an atomic save across editors. The compile-ready example uses local-only plugin data and bounded atomic files. For multi-owner storage, partition by a verified stable owner identity rather than assuming conversationId identifies every Worker. Cancellation and Uncertain do not prove rollback; reopen/read authoritative state before another save, and never automatically replay. Existing API-v1 plugins remain compatible. A new plugin referencing this interface requires a host SDK containing these additive types; a runtime without editor hosting will not mount the contribution. Do not reparent ConfigurationPanels, use a private Worker DTO, or add a generic service operation.",
                    signatures = ConversationSettingsContract,
                    example = ConversationSettingsExample
                },
                "settings-navigation" => new
                {
                    summary = "Optional scoped host navigation to this plugin's declared global Configuration panel. Supply only the panel ID; the host supplies plugin identity and loaded package generation. Availability only checks whether the current route can attempt navigation. Only Completed confirms the matching Configuration panel is visible and ready. Accepted or Uncertain may have changed the UI; inspect Configuration before another action and never automatically replay. Keep this separate from per-chat conversation-settings editors. A runtime without the optional host facet cannot open the panel through this route.",
                    signatures = SettingsNavigationContract,
                    example = SettingsNavigationExample
                },
                "composer-command" => new
                {
                    summary = "Prefix commands are separate from visible ComposerActions. Pass preserves input; Continue consumes the exact leading character count and submits the suffix.",
                    signatures = ComposerCommandContract,
                    example = ComposerCommandExample
                },
                _ => throw new ArgumentException("Unknown plugin contract topic.")
            };
            return ToolPolicy.Result(new
            {
                topic,
                contract = NativeDesktopPluginPackage.ContractId,
                apiVersion = NativeDesktopPluginPackage.ApiVersion,
                reference,
                theme = ToolModelProjection.DesktopPluginThemeGuidance,
                editing = "Use file.write with path, expectedSha256, collision=replace, and content to replace an existing whole source file from a complete-file example. Use file.edit only with path, expectedSha256, oldText, and newText for a bounded exact replacement. A class-only replacement must begin at the class declaration and must not repeat using directives or a file-scoped namespace. Never place two file-scoped namespaces in one source file. After a confirmed write or edit, advance to the build without rereading the unchanged file.",
                forbiddenGuesses = new[] { "Cerebral.Desktop.State", "ConversationSnapshot", "StatusBarPanel", "StatusBarItem" }
            }) with { Evidence = new(ToolExecutionOutcome.Succeeded, "cerebral.plugin-sdk", State: "contract") };
        }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ToolResult(false, error.Message)
            {
                Evidence = new(ToolExecutionOutcome.Rejected, "cerebral.plugin-sdk", State: "rejected")
            };
        }
    }

    private ToolResult Projects(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ToolPolicy.Grant(context.Profile, ProjectsToolName, "filesystem.read");
            RequirePluginAuthor(context.Profile, "Plugin project inventory");
            if (call.Arguments.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Plugin project inventory accepts one object.");
            var arguments = call.Arguments.EnumerateObject().ToArray();
            if (arguments.Any(property => property.Name is not ("root" or "targetPath" or "continuation")) ||
                arguments.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != arguments.Length ||
                arguments.Length > 1)
                throw new ArgumentException("Plugin project inventory accepts only one optional root, targetPath, or continuation property.");
            string? requestedRoot = null;
            string? continuation = null;
            if (call.Arguments.TryGetProperty("continuation", out var continuationValue))
            {
                if (continuationValue.ValueKind != JsonValueKind.String ||
                    continuationValue.GetString() is not { Length: 32 } value ||
                    !value.All(char.IsAsciiHexDigit))
                    throw new ArgumentException("Inventory continuation must be the exact opaque 32-character token returned by plugin.projects.");
                continuation = value;
            }
            if (call.Arguments.TryGetProperty("root", out var rootValue))
            {
                if (rootValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(rootValue.GetString()))
                    throw new ArgumentException("Plugin project inventory root must be a non-empty absolute path.");
                requestedRoot = rootValue.GetString();
                if (!Path.IsPathFullyQualified(requestedRoot!))
                    throw new ArgumentException("Plugin project inventory root must be an absolute generated-project path. For discovery, send name=plugin.projects with arguments={} (omit root key); do not put braces or a project name in root.");
            }

            var projectContext = context with { FullAccess = false };
            string? targetPath = null, targetComponent = null;
            if (call.Arguments.TryGetProperty("targetPath", out _))
            {
                targetPath = ToolPolicy.PathInScope(ToolPolicy.Text(call.Arguments, "targetPath"), projectContext, ScopedPathAccess.Read);
                var target = ResolveTargetProject(targetPath, projectContext, cancellationToken);
                if (target is null)
                    return ToolPolicy.Result(new { targetPath, targetMatched = false }) with
                    { Evidence = new(ToolExecutionOutcome.Succeeded, "cerebral.plugin-sdk", State: "target-inspected") };
                requestedRoot = target.Value.Root;
                targetComponent = target.Value.Component;
            }
            if (requestedRoot is null) inventoriedJobs.TryRemove(context.JobId, out _);
            var retainedAdmission = false;
            PluginProjectInventory inventory;
            lock (SourceInventoryGate)
            {
                if (continuation is null && requestedRoot is null)
                    pendingInventories.Remove(context.JobId);
                if (requestedRoot is not null)
                {
                    string exactRoot;
                    try
                    {
                        exactRoot = ToolPolicy.PathInScope(requestedRoot, projectContext, ScopedPathAccess.Read, mustExist: true);
                    }
                    catch (FileNotFoundException error)
                    {
                        // Keep scope, link and existence admission intact while correcting a
                        // proposed new-directory call before the ordinary rejection clears admission.
                        throw new DirectoryNotFoundException(error.Message + ". For a new plugin, call plugin.projects with {} (omit root); root is only for an existing generated plugin containing PLUGIN.md.", error);
                    }
                    if (!Directory.Exists(exactRoot))
                        throw new DirectoryNotFoundException("The requested plugin project root does not exist. For a new plugin, call plugin.projects with {} (omit root); root is only for an existing generated plugin containing PLUGIN.md.");
                    ToolPolicy.RejectLinks(exactRoot);
                    if (!File.Exists(Path.Combine(exactRoot, "PLUGIN.md")))
                        throw new InvalidDataException("The requested root is not a generated plugin source project containing PLUGIN.md. For a new plugin, call plugin.projects with {} (omit root), not the parent Projects folder.");
                    var inspected = InspectProject(exactRoot, includeSdkReference: true);
                    if (targetComponent is not null)
                        inspected = inspected with { Components = inspected.Components.Where(component => component.Component == targetComponent).ToArray() };
                    inventory = new([inspected], [], 1, false, null);
                    retainedAdmission = inventoriedJobs.TryGetValue(context.JobId, out var admission) &&
                        ReviewedProjectRoots(projectContext).SequenceEqual(admission.Roots, PathComparer) &&
                        inventory.Projects[0].Components.Count > 0 &&
                        inventory.Projects[0].Components.All(HasValidComponentIdentity);
                    if (!retainedAdmission) inventoriedJobs.TryRemove(context.JobId, out _);
                }
                else
                {
                    InventoryScanState scan;
                    if (continuation is null)
                    {
                        foreach (var stale in pendingInventories.Where(entry =>
                                     DateTimeOffset.UtcNow - entry.Value.CreatedUtc > InventoryContinuationLifetime)
                                     .Select(entry => entry.Key).ToArray())
                            pendingInventories.Remove(stale);
                        if (pendingInventories.Count >= 32)
                            throw new InvalidOperationException("Too many pending plugin inventories. Finish or expire an existing bounded continuation.");
                        scan = BeginInventory(projectContext, cancellationToken);
                    }
                    else if (!pendingInventories.TryGetValue(context.JobId, out var existingScan) ||
                        existingScan.Token != continuation ||
                        DateTimeOffset.UtcNow - existingScan.CreatedUtc > InventoryContinuationLifetime ||
                        !ReviewedProjectRoots(projectContext).SequenceEqual(existingScan.Roots, PathComparer))
                    {
                        pendingInventories.Remove(context.JobId);
                        throw new InvalidOperationException("Inventory continuation expired or its reviewed roots changed. Restart plugin.projects with {}.");
                    }
                    else scan = existingScan;
                    inventory = ScanProjects(projectContext, scan, cancellationToken);
                    if (inventory.Continuation is null) pendingInventories.Remove(context.JobId);
                    else pendingInventories[context.JobId] = scan;
                }
            }

            var next = requestedRoot is null && inventory.TraversalComplete && inventory.Limited
                ? "All reviewed roots were scanned, but detailed project rows exceed the bounded response. Read the complete identitySummary and its count; scaffold only when identityScaffoldReady is true and no suitable source matches. Inspect any matching exact root before editing or building."
                : requestedRoot is null && inventory.Continuation is not null
                ? "Inventory is incomplete. Continue this same job with plugin.projects {\"continuation\":\"<exact returned token>\"}; inspect any matching returned root exactly. Partial pages never prove absence or permit scaffolding."
                : requestedRoot is null
                ? "Discovery only: no project or build is selected yet. Examining an existing example does not choose it for modification. For a new named plugin when existing plugins must be preserved, use its own absent scaffold target and distinct ID/name after complete, untruncated discovery; unrelated sources remain reference examples only. Select a row matching the requested existing plugin, copy its absolute root verbatim, and immediately call plugin.projects again with {\"root\":\"<exact returned root>\"} before reading, editing, building, verifying, or presenting it. Never substitute an id, name, basename, component directory, manifest, or guessed path. If limited is true or this result is truncated, a missing row does not prove that no suitable source exists; do not scaffold from that absence. When new scaffolding is unavailable, do not construct another Cerebral plugin project with ordinary file operations. Reuse a genuinely matching source or exact-inspect the reported failing root, repair identity metadata only from independently established evidence, then refresh broad discovery. If identity remains unknown, report that blocker."
                : "This exact-root inspection revalidated the selected project. It supplies reference coordinates; examining an existing example does not choose it for modification or make it the requested plugin. If this inspected source is unrelated to the requested new named plugin and existing plugins must be preserved, keep this source as a reference and scaffold the requested plugin's own absent target with distinct ID/name after complete, untruncated discovery. Its component build objects are the authoritative process.run executableId, arguments, workingDirectory, and builtManifest; copy those coordinates unchanged before editing, building, verifying, or presenting the project. This exact read did not establish or refresh broad source admission. " +
                    (retainedAdmission ? "The preceding complete broad admission remains available; scaffolding will recheck every reviewed root and identity before writing."
                        : "A new scaffold still requires complete, untruncated broad discovery.");
            var desktopAuthoringContract = requestedRoot is not null && inventory.Projects.Count == 1 &&
                inventory.Projects[0].Components.Any(component => component.Component == DesktopComponent &&
                    component.Source is not null && component.Build is not null)
                ? ToolModelProjection.DesktopPluginAuthoringContractId : null;
            // Discovery carries selection coordinates only. Build recipes and source paths belong
            // to the exact-root inspection; repeating them for every project can truncate a complete catalog.
            object projects = requestedRoot is null
                ? inventory.Projects.Select(project => new
                {
                    project.Root, project.Id, project.Name, project.Version,
                    Components = project.Components.Select(component => new { component.Component, component.Error }).ToArray(),
                    project.Error
                }).ToArray()
                : inventory.Projects;
            var identitySummary = requestedRoot is null && inventory.TraversalComplete
                ? inventory.AllProjects!.Select(project => new
                {
                    project.Root, project.Id, project.Name,
                    Components = project.Components.Select(component => new
                    { component.Component, component.Id, component.Name }).ToArray()
                }).ToArray()
                : null;
            var identityReadyResult = identitySummary is not null;
            var responseReason = inventory.TraversalComplete && inventory.Limited
                ? "Detailed project rows exceed the bounded response; the compact identity summary covers every reviewed source."
                : inventory.IncompleteReason;
            var result = ToolPolicy.Result(new
            {
                projects,
                identitySummary,
                identitySummaryComplete = identityReadyResult,
                identityScaffoldReady = identityReadyResult,
                inventoryJobId = identityReadyResult ? context.JobId : null,
                targetPath,
                targetMatched = targetPath is null ? (bool?)null : true,
                targetComponent,
                discoverySummaryOnly = requestedRoot is null,
                scaffoldReady = requestedRoot is null ? !inventory.Limited : retainedAdmission,
                count = inventory.Projects.Count,
                scannedDirectories = inventory.ScannedDirectories,
                inventoryId = inventory.InventoryId,
                inventoryPage = inventory.InventoryPage,
                inventoryComplete = requestedRoot is null ? inventory.TraversalComplete : (bool?)null,
                totalProjectsFound = inventory.TotalProjectsFound,
                totalDirectoriesScanned = inventory.TotalDirectoriesScanned,
                continuation = inventory.Continuation,
                limited = inventory.Limited,
                incompleteReason = responseReason,
                desktopAuthoringContract,
                next
            });
            if (requestedRoot is null && result.Truncated)
            {
                // Drop detailed rows before giving up on a bounded complete identity list.
                // An old caller still sees limited=true and scaffoldReady=false.
                if (identitySummary is not null)
                {
                    var compact = ToolPolicy.Result(new
                    {
                        projects = Array.Empty<object>(),
                        identitySummary,
                        identitySummaryComplete = true,
                        identityScaffoldReady = true,
                        inventoryJobId = context.JobId,
                        targetPath = (string?)null,
                        targetMatched = (bool?)null,
                        targetComponent = (string?)null,
                        discoverySummaryOnly = true,
                        scaffoldReady = false,
                        count = 0,
                        scannedDirectories = inventory.ScannedDirectories,
                        inventoryId = inventory.InventoryId,
                        inventoryPage = inventory.InventoryPage,
                        inventoryComplete = inventory.TraversalComplete,
                        totalProjectsFound = inventory.TotalProjectsFound,
                        totalDirectoriesScanned = inventory.TotalDirectoriesScanned,
                        continuation = (string?)null,
                        limited = true,
                        incompleteReason = "Detailed project rows were omitted to fit the complete compact identity summary.",
                        desktopAuthoringContract = (string?)null,
                        next = "Inspect the complete identitySummary for a suitable source. Scaffold only if identityScaffoldReady is true and no source matches."
                    });
                    if (compact.Success && !compact.Truncated) result = compact;
                }
                if (result.Truncated)
                {
                    identityReadyResult = false;
                    // A clipped aggregate could make a missing identity appear absent.
                    result = ToolPolicy.Result(new
                    {
                        projects = Array.Empty<object>(),
                        identitySummary = (object?)null,
                        identitySummaryComplete = false,
                        identityScaffoldReady = false,
                        inventoryJobId = (string?)null,
                        targetPath = (string?)null,
                        targetMatched = (bool?)null,
                        targetComponent = (string?)null,
                        discoverySummaryOnly = true,
                        scaffoldReady = false,
                        count = 0,
                        scannedDirectories = inventory.ScannedDirectories,
                        inventoryId = inventory.InventoryId,
                        inventoryPage = inventory.InventoryPage,
                        inventoryComplete = inventory.TraversalComplete,
                        totalProjectsFound = inventory.TotalProjectsFound,
                        totalDirectoriesScanned = inventory.TotalDirectoriesScanned,
                        continuation = (string?)null,
                        limited = true,
                        incompleteReason = "The complete source identity response exceeded its bounded output. Narrow the reviewed source roots; no scaffold admission was granted.",
                        desktopAuthoringContract = (string?)null,
                        next = "Inventory output was truncated. Do not infer a missing plugin source or scaffold from this result."
                    }) with { Truncated = true };
                }
            }
            // Exact reads may retain an existing broad admission, never mint or restore one.
            // The writer still rechecks current roots, every identity, and target absence.
            if (!result.Success || result.Truncated)
            {
                inventoriedJobs.TryRemove(context.JobId, out _);
                lock (SourceInventoryGate) pendingInventories.Remove(context.JobId);
            }
            else if (requestedRoot is null && identityReadyResult)
                inventoriedJobs[context.JobId] = new(inventory.Roots);
            return result with { Evidence = new(ToolExecutionOutcome.Succeeded, "cerebral.plugin-sdk", State: "inventoried") };
        }
        catch (OperationCanceledException)
        {
            inventoriedJobs.TryRemove(context.JobId, out _);
            lock (SourceInventoryGate) pendingInventories.Remove(context.JobId);
            throw;
        }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or IOException or InvalidDataException or InvalidOperationException or BadImageFormatException or JsonException)
        {
            inventoriedJobs.TryRemove(context.JobId, out _);
            lock (SourceInventoryGate) pendingInventories.Remove(context.JobId);
            return new ToolResult(false, error.Message)
            {
                Evidence = new(ToolExecutionOutcome.Rejected, "cerebral.plugin-sdk", State: "rejected")
            };
        }
    }

    // Source ownership is the SDK's fixed scaffold structure, never a project basename or
    // prompt intent. This lookup imports no build/install credit and never discovers siblings.
    private static (string Root, string Component)? ResolveTargetProject(string targetPath,
        ToolExecutionContext context, CancellationToken cancellationToken)
    {
        ToolPolicy.RejectLinks(targetPath);
        var ancestor = Directory.Exists(targetPath) ? targetPath : Path.GetDirectoryName(targetPath);
        for (var depth = 0; ancestor is not null && depth < 32; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string admitted;
            try { admitted = ToolPolicy.PathInScope(ancestor, context, ScopedPathAccess.Read, mustExist: false); }
            catch (UnauthorizedAccessException) { return null; }
            ToolPolicy.RejectLinks(admitted);
            var guide = Path.Combine(admitted, "PLUGIN.md");
            var hasGuide = InventoryFileExists(guide);
            // Fixed SDK residue remains owned when an edit has removed required metadata.
            // A copied SDK plus canonical project distinguishes a broken scaffold from ordinary .NET files.
            var markers = new[] { DesktopComponent, ServiceComponent }.Select(component =>
            {
                var desktop = component == DesktopComponent;
                var directory = Path.Combine(admitted, desktop ? "Desktop" : "Service");
                var sourceOrManifest = InventoryFileExists(Path.Combine(directory, desktop
                    ? NativeDesktopPluginPackage.ManifestFileName : "service-plugin.json")) ||
                    InventoryFileExists(Path.Combine(directory, desktop ? "GeneratedDesktopPlugin.cs" : "GeneratedServicePlugin.cs"));
                var copiedProject = InventoryFileExists(Path.Combine(admitted, "sdk", component, desktop
                        ? "Cerebral.Desktop.Plugins.dll" : "Cerebral.Core.dll")) &&
                    HasScaffoldProjectMarker(directory, component);
                return (Component: component, Recognized: hasGuide && sourceOrManifest || copiedProject);
            }).Where(marker => marker.Recognized).ToArray();
            if (markers.Length > 0)
            {
                if (!hasGuide)
                    throw new InvalidDataException("The SDK source project is missing PLUGIN.md at root=" + admitted + ".");
                ToolPolicy.RejectLinks(guide);
                var project = InspectProject(admitted);
                if (project.Error is not null || project.Components.Count == 0 ||
                    markers.Any(marker => !project.Components.Any(component => component.Component == marker.Component)) ||
                    project.Components.Any(component => component.Error is not null ||
                        component.Source is null || component.Build is null || !HasValidComponentIdentity(component)))
                    throw new InvalidDataException("The SDK source project is malformed at root=" + admitted +
                        ". Exact-inspect this root with plugin.projects and repair its source metadata from actual evidence.");
                var direct = project.Components.Where(component =>
                    ScopedFilePolicy.IsWithin(targetPath, Path.GetDirectoryName(component.Manifest)!)).ToArray();
                var selected = direct.Length == 1 ? direct[0] : project.Components.Count == 1 ? project.Components[0] : null;
                if (selected is null)
                    throw new InvalidDataException("The SDK source target selects no unique component at root=" + admitted +
                        ". Use an exact returned Desktop or Service source/project coordinate.");
                var sdk = Path.Combine(admitted, "sdk", selected.Component, selected.Component == DesktopComponent
                    ? "Cerebral.Desktop.Plugins.dll" : "Cerebral.Core.dll");
                if (!File.Exists(sdk))
                    throw new InvalidDataException("The SDK source project is missing its copied public SDK at root=" + admitted + ".");
                ToolPolicy.RejectLinks(sdk);
                return (admitted, selected.Component);
            }
            var parent = Path.GetDirectoryName(admitted);
            if (parent is null || PathComparer.Equals(parent, admitted)) return null;
            ancestor = parent;
        }
        throw new InvalidDataException("The exact SDK target lookup reached its bounded ancestor limit; no authoring action was dispatched.");
    }

    private static InventoryScanState BeginInventory(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var scan = new InventoryScanState(ReviewedProjectRoots(context));
        if (context.Profile.AllowedRoots.Any(root => !Path.IsPathFullyQualified(root)))
            scan.MarkIncomplete("A reviewed project root is not an absolute path; complete source discovery is unavailable.");
        if (scan.Roots.Length == 0)
            scan.MarkIncomplete("No reviewed project roots are configured for complete source discovery.");
        foreach (var configuredRoot in scan.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var attributes = File.GetAttributes(configuredRoot);
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    scan.MarkIncomplete($"Reviewed project root is not a directory: '{configuredRoot}'.");
                    continue;
                }
                var root = ToolPolicy.PathInScope(configuredRoot, context, ScopedPathAccess.Read, mustExist: true);
                if (scan.Queued.Count >= MaximumInventoryDirectories * MaximumInventoryPages)
                {
                    scan.MarkIncomplete("Bounded continuation directory limit reached while admitting reviewed roots.");
                    break;
                }
                if (scan.Queued.Add(root)) scan.Pending.Enqueue(root);
            }
            catch (FileNotFoundException)
            { scan.MarkIncomplete($"Reviewed project root is missing: '{configuredRoot}'."); }
            catch (DirectoryNotFoundException)
            { scan.MarkIncomplete($"Reviewed project root is missing: '{configuredRoot}'."); }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException)
            {
                scan.MarkIncomplete($"Cannot inspect reviewed project root '{configuredRoot}': {error.Message}");
            }
        }
        return scan;
    }

    // A page only advances the current job's read-only inventory. No partial page grants
    // a source write; the writer performs its own fresh complete scan under SourceInventoryGate.
    private static PluginProjectInventory ScanProjects(ToolExecutionContext context, InventoryScanState scan,
        CancellationToken cancellationToken)
    {
        var projects = new List<PluginProjectSummary>();
        var scanned = 0;
        while (scan.IncompleteReason is null && scan.Pending.Count > 0 &&
               scanned < MaximumInventoryDirectories && projects.Count < MaximumInventoryProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = scan.Pending.Dequeue();
            if (!scan.Seen.Add(current)) continue;
            scanned++;
            scan.ScannedDirectories++;
            try
            {
                ToolPolicy.RejectLinks(current);
                if (InventoryFileExists(Path.Combine(current, "PLUGIN.md")))
                {
                    var project = InspectProject(current);
                    projects.Add(project);
                    scan.Projects.Add(project);
                    if (project.Components.Count == 0)
                        scan.MarkIncomplete($"Plugin source has no readable component identity at '{current}'.");
                    foreach (var component in project.Components)
                    {
                        if (!HasValidComponentIdentity(component))
                            scan.MarkIncomplete($"Cannot establish plugin identity from '{component.Manifest}': {component.Error ?? "missing or invalid ID/display name"}.");
                    }
                }
                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (scan.IncompleteReason is not null) break;
                    var name = Path.GetFileName(child);
                    // These conventional build/dependency folders are not source project roots.
                    if (name is ".git" or "bin" or "obj" or "node_modules" or ".packages") continue;
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    if (scan.Queued.Contains(child)) continue;
                    if (scan.Queued.Count >= MaximumInventoryDirectories * MaximumInventoryPages)
                    {
                        scan.MarkIncomplete($"Bounded continuation directory limit reached at '{child}'.");
                        break;
                    }
                    scan.Queued.Add(child);
                    scan.Pending.Enqueue(child);
                }
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or InvalidDataException or InvalidOperationException)
            {
                scan.MarkIncomplete($"Cannot complete source inventory at '{current}': {error.Message}");
            }
        }
        scan.Pages++;
        if (scan.Pending.Count > 0 && scan.Pages >= MaximumInventoryPages && scan.IncompleteReason is null)
            scan.MarkIncomplete($"Bounded continuation page limit ({MaximumInventoryPages}) reached.");
        var traversalComplete = scan.Pending.Count == 0 && scan.IncompleteReason is null;
        // Legacy ready/limited describe the complete returned identity catalog, not just
        // the last page of a traversal. Too many rows can never mint admission.
        var complete = traversalComplete && scan.Projects.Count <= MaximumInventoryProjects;
        if (!traversalComplete && scan.IncompleteReason is null) scan.RotateToken();
        var continuation = !traversalComplete && scan.IncompleteReason is null ? scan.Token : null;
        var rows = complete
            ? scan.Projects : projects;
        var reason = traversalComplete && !complete
            ? $"Complete traversal found {scan.Projects.Count} plugin sources; the complete identity catalog exceeds the {MaximumInventoryProjects}-row response bound. Narrow the reviewed source roots; absence and scaffold admission remain unavailable."
            : scan.IncompleteReason ?? (projects.Count >= MaximumInventoryProjects && scan.Pending.Count > 0
            ? $"Plugin source limit ({MaximumInventoryProjects}) reached for this page; continue with the exact returned token."
            : "Inventory has more pages; continue with the exact returned token.");
        return new(rows.AsReadOnly(), scan.Roots, scanned, !complete, complete ? null : reason)
        {
            Continuation = continuation,
            TotalProjectsFound = scan.Projects.Count,
            TotalDirectoriesScanned = scan.ScannedDirectories,
            InventoryId = scan.Id,
            InventoryPage = scan.Pages,
            TraversalComplete = traversalComplete,
            AllProjects = scan.Projects.AsReadOnly()
        };
    }

    // The final source mutation uses a new scan over every reviewed root, not a union of
    // previously returned pages. Its bounded internal pages produce no large tool payload.
    private static PluginProjectInventory DiscoverProjects(ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var scan = BeginInventory(context, cancellationToken);
        PluginProjectInventory page;
        do
        {
            page = ScanProjects(context, scan, cancellationToken);
        }
        while (page.Continuation is not null);
        return new(scan.Projects.AsReadOnly(), scan.Roots, scan.ScannedDirectories,
            page.Limited, page.IncompleteReason)
        {
            TotalProjectsFound = scan.Projects.Count,
            TotalDirectoriesScanned = scan.ScannedDirectories,
            InventoryId = scan.Id,
            InventoryPage = scan.Pages,
            TraversalComplete = page.TraversalComplete,
            AllProjects = scan.Projects.AsReadOnly()
        };
    }
    private static string[] ReviewedProjectRoots(ToolExecutionContext context) =>
        context.Profile.AllowedRoots.Where(Path.IsPathFullyQualified).Select(Path.GetFullPath)
            .Distinct(PathComparer).Order(PathComparer).ToArray();

    private static bool HasValidComponentIdentity(PluginComponentSummary component)
    {
        try
        {
            ValidateIdentity(component.Id ?? "", component.Name ?? "", component.Component);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException) { return false; }
    }

    private static bool InventoryFileExists(string path)
    {
        // File.Exists conflates absence with denied access. Only actual missing paths
        // may be omitted from the source identity inventory.
        try { return (File.GetAttributes(path) & FileAttributes.Directory) == 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static PluginProjectSummary InspectProject(string root, bool includeSdkReference = false)
    {
        var components = new List<PluginComponentSummary>();
        Add("desktop", Path.Combine(root, "Desktop", NativeDesktopPluginPackage.ManifestFileName));
        Add("service", Path.Combine(root, "Service", "service-plugin.json"));
        var identities = components.Where(item => item.Id is not null).Select(item => item.Id!).Distinct(StringComparer.Ordinal).ToArray();
        var versions = components.Where(item => item.Version is not null).Select(item => item.Version!).Distinct(StringComparer.Ordinal).ToArray();
        return new(root, identities.Length == 1 ? identities[0] : null,
            components.Select(item => item.Name).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            versions.Length == 1 ? versions[0] : null, components.AsReadOnly(),
            identities.Length > 1 ? "Component package IDs do not match." : versions.Length > 1 ? "Component versions do not match." : null);

        void Add(string kind, string manifestPath)
        {
            string? source = null;
            PluginBuildSummary? build = null;
            try
            {
                if (!InventoryFileExists(manifestPath)) return;
                ToolPolicy.RejectLinks(manifestPath);
                var directory = Path.GetDirectoryName(manifestPath)!;
                var generatedSource = Path.Combine(directory, kind == DesktopComponent
                    ? "GeneratedDesktopPlugin.cs" : "GeneratedServicePlugin.cs");
                source = File.Exists(generatedSource) ? generatedSource : null;
                if (source is not null) ToolPolicy.RejectLinks(source);
                var project = SelectComponentProject(directory);
                build = project is null ? null : BuildSummary(directory, project, kind, Path.GetFileName(manifestPath));
                using var input = File.OpenRead(manifestPath);
                var bytes = new byte[64 * 1024 + 1];
                var length = 0;
                while (length < bytes.Length)
                {
                    var read = input.Read(bytes, length, bytes.Length - length);
                    if (read == 0) break;
                    length += read;
                }
                if (length > 64 * 1024) throw new InvalidDataException("Manifest exceeds 64 KiB.");
                using var document = JsonDocument.Parse(ToolPolicy.Decode(bytes[..length]));
                var manifest = document.RootElement;
                string? Text(string property) => manifest.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null;
                components.Add(new(kind, manifestPath, source, build, Text("id"), Text("name"), Text("version"),
                    project is null ? "The component has no project file in its manifest directory." : null)
                {
                    SdkReference = includeSdkReference && kind == ServiceComponent ? ServiceSdkReference.Inspect(root) : null
                });
            }
            catch (JsonException error)
            {
                // Invalid metadata still needs source repair. Geometry came from scoped ordinary
                // files above; no package identity, verification, or installation is inferred.
                components.Add(new(kind, manifestPath, source, build, null, null, null, error.Message));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                          InvalidDataException or ArgumentException or InvalidOperationException)
            {
                components.Add(new(kind, manifestPath, null, null, null, null, null, error.Message));
            }
        }
    }

    private static PluginBuildSummary BuildSummary(string directory, string project, string component, string manifest) =>
        new("dotnet", ["build", project, "--configuration", "Release", "--disable-build-servers", "--verbosity", "minimal"],
            directory, Path.Combine(directory, "bin", "Release", component == "desktop" ? "net10.0-windows" : "net10.0", manifest));

    internal static bool CanAuthorPlugin(AgentProfile profile) =>
        profile.Role is ("lead" or "helper") && profile.WorkerId == "local" &&
        profile.Capabilities.Contains("desktop.plugins.manage", StringComparer.Ordinal);

    private static void RequirePluginAuthor(AgentProfile profile, string operation)
    {
        if (!CanAuthorPlugin(profile))
            throw new UnauthorizedAccessException(operation + " requires a local lead or helper profile and its explicit desktop.plugins.manage capability.");
    }

    // Verification reads package metadata only. Malformed manifest JSON is a deterministic
    // rejection, not an uncertain execution outcome that requires effect reconciliation.
    private static ToolResult Verify(ToolCall call, ToolExecutionContext context)
    {
        try
        {
            ToolPolicy.Grant(context.Profile, VerifyToolName, "filesystem.read");
            RequirePluginAuthor(context.Profile, "Plugin verification");
            var root = ToolPolicy.PathInScope(ToolPolicy.Text(call.Arguments, "root"), context with { FullAccess = false }, ScopedPathAccess.Read);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The plugin source root does not exist.");
            ToolPolicy.RejectLinks(root);
            var desktop = VerifyDesktop(root, context);
            var service = VerifyService(root);
            if (desktop is null && service is null)
                throw new InvalidDataException(VerificationRootDiagnostic(root, context));
            if (desktop is not null && service is not null && desktop.Value.Id != service.Value.Id)
                throw new InvalidDataException("Desktop and service package IDs do not match.");
            if (desktop is not null && service is not null && desktop.Value.Version != service.Value.Version)
                throw new InvalidDataException("Desktop and service package versions do not match.");
            return ToolPolicy.Result(new
            {
                root,
                pluginId = desktop?.Id ?? service!.Value.Id,
                desktop = desktop is null ? null : new { manifest = desktop.Value.Manifest, assembly = desktop.Value.Assembly,
                    packageFingerprint = NativeDesktopPluginPackage.ComputeFingerprint(desktop.Value.Manifest) },
                service = service is null ? null : new { manifest = service.Value.Manifest, assembly = service.Value.Assembly },
                verified = true
            }) with { Evidence = new(ToolExecutionOutcome.Succeeded, "cerebral.plugin-sdk", State: "verified") };
        }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or IOException or InvalidDataException or InvalidOperationException or BadImageFormatException or JsonException)
        {
            return new ToolResult(false, error.Message)
            {
                Evidence = new(ToolExecutionOutcome.Rejected, "cerebral.plugin-sdk", State: "rejected")
            };
        }
    }

    private static string VerificationRootDiagnostic(string root, ToolExecutionContext context)
    {
        const string message = "The plugin root contains no generated Desktop or Service component. " +
            "root must be the project directory containing PLUGIN.md and Desktop/Service, not a component or build-output directory.";
        // Offer an exact correction only from a bounded, admitted ancestor. This is
        // guidance, never automatic verification of a different caller-selected root.
        var candidate = Directory.GetParent(root);
        for (var depth = 0; candidate is not null && depth < 6; depth++, candidate = candidate.Parent)
        {
            string admitted;
            try
            {
                admitted = ToolPolicy.PathInScope(candidate.FullName, context with { FullAccess = false }, ScopedPathAccess.Read);
                ToolPolicy.RejectLinks(admitted);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or ArgumentException)
            {
                break;
            }
            if (File.Exists(Path.Combine(admitted, "PLUGIN.md")) &&
                (File.Exists(Path.Combine(admitted, "Desktop", NativeDesktopPluginPackage.ManifestFileName)) ||
                 File.Exists(Path.Combine(admitted, "Service", "service-plugin.json"))))
                return message + " Retry plugin.verify with root=" + admitted;
        }
        return message + " Use the exact root from plugin.projects.";
    }

    /// <summary>Recheck generated Release packages before queueing or claiming them. Imported packages have no source dependency.</summary>
    public static void ValidateDesktopPresentation(AgentProfile profile, string manifestPath, bool fullAccess) =>
        _ = ValidateDesktopPresentationSource(profile, manifestPath, fullAccess);

    /// <summary>Returns an optional exact SDK source coordinate only after the existing scoped package validation.
    /// This admission-time reference is not a current authoring or build envelope.</summary>
    public static string? ValidateDesktopPresentationSource(AgentProfile profile, string manifestPath, bool fullAccess) =>
        ValidateDesktopPresentationSourceCore(
            new(profile, "interface", "interface", FullAccess: fullAccess), manifestPath, validateManifestScope: false);

    /// <summary>Preserve the caller's operation-aware scope while inspecting package/source metadata.
    /// This validates paths only; the Worker separately admits the presentation's durable owner.</summary>
    public static string? ValidateDesktopPresentationSource(ToolExecutionContext context, string manifestPath) =>
        ValidateDesktopPresentationSourceCore(context, manifestPath, validateManifestScope: true);

    private static string? ValidateDesktopPresentationSourceCore(ToolExecutionContext context, string manifestPath,
        bool validateManifestScope)
    {
        // The legacy overload returns null for non-SDK layouts without inspecting them. The
        // contextual overload must admit even an imported manifest before returning that result.
        if (validateManifestScope)
            manifestPath = ToolPolicy.PathInScope(manifestPath, context, ScopedPathAccess.Read, mustExist: true);
        var output = Path.GetDirectoryName(manifestPath)!;
        var release = Path.GetDirectoryName(output);
        var bin = release is null ? null : Path.GetDirectoryName(release);
        var desktop = bin is null ? null : Path.GetDirectoryName(bin);
        if (!Path.GetFileName(output).Equals("net10.0-windows", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(release), "Release", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(bin), "bin", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(desktop), "Desktop", StringComparison.OrdinalIgnoreCase)) return null;
        var root = Path.GetDirectoryName(desktop!)!;
        // Checking a manifest does not grant reads of its source ancestors. Apply the original
        // presentation scope before inspecting this known SDK output layout.
        _ = ToolPolicy.PathInScope(root, context, ScopedPathAccess.Read, mustExist: true);
        _ = VerifyDesktop(root, validateManifestScope ? context : null) ?? throw new InvalidDataException("The generated desktop source is missing.");
        // Do not advertise a parent/component guess when an exact SDK inspection cannot recover
        // its guide and project. These optional reference checks do not admit any package bytes.
        var guide = Path.Combine(root, "PLUGIN.md");
        if (!File.Exists(guide)) return null;
        ToolPolicy.RejectLinks(guide);
        var project = SelectComponentProject(desktop!);
        if (project is null) return null;
        return root;
    }

    private static (string Id, string Version, string Manifest, string Assembly)? VerifyDesktop(string root, ToolExecutionContext? context = null)
    {
        var directory = Path.Combine(root, "Desktop");
        if (context is not null) _ = ToolPolicy.PathInScope(directory, context, ScopedPathAccess.Read);
        if (!Directory.Exists(directory)) return null;
        var project = SelectComponentProject(directory) ??
            throw new InvalidDataException("The desktop component has no project file. Exact-inspect this source root before building.");
        var manifestPath = Path.Combine(directory, "bin", "Release", "net10.0-windows", NativeDesktopPluginPackage.ManifestFileName);
        if (context is not null) _ = ToolPolicy.PathInScope(manifestPath, context, ScopedPathAccess.Read);
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("Build required before verification: the Release desktop manifest does not exist. " +
                "Call process.run with executableId=dotnet, workingDirectory=" + directory +
                ", and separate arguments: build, " + project +
                ", --configuration, Release, --disable-build-servers, --verbosity, minimal. " +
                "Repair actual compiler errors, then call plugin.verify again on " + root + ".");
        var manifest = NativeDesktopPluginPackage.Inspect(manifestPath);
        var assembly = Path.Combine(Path.GetDirectoryName(manifestPath)!, manifest.Assembly);
        if (context is not null) _ = ToolPolicy.PathInScope(assembly, context, ScopedPathAccess.Read, mustExist: true);
        VerifyFreshOutput(directory, manifestPath, assembly, "desktop", context);
        NativeDesktopPluginPackage.ValidateEntryPoint(manifest);
        VerifyDesktopImplementation(directory, manifest, context);
        return (manifest.Id, manifest.Version, manifestPath, assembly);
    }

    private static void VerifyDesktopImplementation(string directory, NativeDesktopPluginIdentity identity, ToolExecutionContext? context)
    {
        var sourcePath = Path.Combine(directory, "GeneratedDesktopPlugin.cs");
        if (context is not null) _ = ToolPolicy.PathInScope(sourcePath, context, ScopedPathAccess.Read);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The generated desktop source is missing.", sourcePath);
        var source = File.ReadAllText(sourcePath);
        var emptyPanels = source.Contains("Panels { get; } = [];", StringComparison.Ordinal);
        var emptyActions = source.Contains("ComposerActions { get; } = [];", StringComparison.Ordinal);
        var emptyPresentation = source.Contains("CreateMessagePresentation(DesktopMessageSnapshot message) => null", StringComparison.Ordinal);
        var shape = ReadDesktopEntryShape(identity);
        if ((emptyPanels && emptyActions && emptyPresentation ||
             shape.DerivesDesktopPluginBase && !shape.OverridesBaseContribution) &&
            !shape.ImplementsSpecializedContribution)
            throw new InvalidDataException("The desktop source is still the inert scaffold and contributes no UI, command, input hook, workspace, configuration view, or message presentation. Read the needed plugin.contract topic, implement the requested contribution, rebuild, and verify again.");
    }

    private static (bool DerivesDesktopPluginBase, bool OverridesBaseContribution,
        bool ImplementsSpecializedContribution) ReadDesktopEntryShape(NativeDesktopPluginIdentity identity)
    {
        const int maximumTypes = 256;
        const int maximumMethods = 4096;
        using var input = File.OpenRead(identity.AssemblyPath);
        using var executable = new PEReader(input);
        if (!executable.HasMetadata)
            throw new InvalidDataException("The desktop assembly has no managed entrypoint metadata.");
        try
        {
            var metadata = executable.GetMetadataReader();
            var entry = default(TypeDefinitionHandle);
            foreach (var handle in metadata.TypeDefinitions)
            {
                if (TypeName(handle) != identity.EntryType) continue;
                if (!entry.IsNil)
                    throw new InvalidDataException("The desktop manifest entryType is ambiguous in its built assembly.");
                entry = handle;
            }
            if (entry.IsNil)
                throw new InvalidDataException("The desktop manifest entryType is absent from its built assembly.");

            var chain = new List<TypeDefinitionHandle>();
            var visitedTypes = new HashSet<TypeDefinitionHandle>();
            var visitedInterfaces = new HashSet<EntityHandle>();
            var implementsSpecializedContribution = false;
            var derivesBase = false;
            var current = entry;
            while (true)
            {
                if (!visitedTypes.Add(current))
                    throw new InvalidDataException("The desktop entry contains a cyclic local inheritance graph.");
                if (visitedTypes.Count > maximumTypes)
                    throw new InvalidDataException("The desktop entry inheritance graph exceeds the metadata inspection limit.");
                chain.Add(current);
                var definition = metadata.GetTypeDefinition(current);
                foreach (var implementationHandle in definition.GetInterfaceImplementations())
                {
                    var implemented = metadata.GetInterfaceImplementation(implementationHandle).Interface;
                    if (HasSpecializedContribution(implemented))
                        implementsSpecializedContribution = true;
                }
                var baseType = definition.BaseType;
                if (IsSdkType(baseType, "DesktopPluginBase"))
                {
                    derivesBase = true;
                    break;
                }
                if (baseType.Kind == HandleKind.TypeDefinition)
                {
                    current = (TypeDefinitionHandle)baseType;
                    continue;
                }
                if (IsSystemObject(baseType)) break;
                if (baseType.Kind == HandleKind.TypeSpecification)
                    throw new InvalidDataException("The desktop entry uses a generic base type whose SDK contribution slots cannot be verified. Derive the entry or a non-generic local base directly from DesktopPluginBase, rebuild, and verify again.");
                throw new InvalidDataException("The desktop entry uses an external base type whose SDK contribution slots cannot be verified. Derive the entry or a local base directly from DesktopPluginBase, rebuild, and verify again.");
            }

            var slots = new Dictionary<string, (bool NearestSdkSlot, bool OverridesSdkSlot)>(StringComparer.Ordinal)
            {
                ["get_Panels"] = (derivesBase, false),
                ["get_ComposerActions"] = (derivesBase, false),
                ["CreateMessagePresentation"] = (derivesBase, false)
            };
            var inspectedMethods = 0;
            for (var index = chain.Count - 1; index >= 0; index--)
            {
                var definition = metadata.GetTypeDefinition(chain[index]);
                var covariantOverrides = new HashSet<MethodDefinitionHandle>();
                foreach (var implementationHandle in definition.GetMethodImplementations())
                {
                    if (++inspectedMethods > maximumMethods)
                        throw new InvalidDataException("The desktop entry inheritance graph exceeds the method inspection limit.");
                    var implementation = metadata.GetMethodImplementation(implementationHandle);
                    if (implementation.MethodDeclaration.Kind != HandleKind.MemberReference) continue;
                    var declaration = metadata.GetMemberReference((MemberReferenceHandle)implementation.MethodDeclaration);
                    if (!IsSdkType(declaration.Parent, "DesktopPluginBase")) continue;
                    var slot = ContributionDeclarationSlot(declaration);
                    if (slot is null || implementation.MethodBody.Kind != HandleKind.MethodDefinition) continue;
                    var bodyHandle = (MethodDefinitionHandle)implementation.MethodBody;
                    var body = metadata.GetMethodDefinition(bodyHandle);
                    if (body.GetDeclaringType() != chain[index] ||
                        (body.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public ||
                        (body.Attributes & MethodAttributes.Virtual) == 0 ||
                        CovariantArrayContributionSlot(body) != slot) continue;
                    covariantOverrides.Add(bodyHandle);
                }
                foreach (var methodHandle in definition.GetMethods())
                {
                    if (++inspectedMethods > maximumMethods)
                        throw new InvalidDataException("The desktop entry inheritance graph exceeds the method inspection limit.");
                    var method = metadata.GetMethodDefinition(methodHandle);
                    var arraySlot = CovariantArrayContributionSlot(method);
                    var slot = BaseContributionSlot(method) ?? arraySlot;
                    if (slot is null) continue;
                    var state = slots[slot];
                    // C# covariant getters may have NewSlot plus an explicit MethodImpl.
                    // Only an exact SDK declaration mapping proves that array body overrides
                    // the SDK slot; same-name hidden properties never gain that credit.
                    if (covariantOverrides.Contains(methodHandle))
                        state.OverridesSdkSlot = true;
                    else if ((method.Attributes & MethodAttributes.Virtual) == 0 ||
                             (method.Attributes & MethodAttributes.NewSlot) != 0)
                        state.NearestSdkSlot = false;
                    else if (state.NearestSdkSlot && arraySlot is null)
                        state.OverridesSdkSlot = true;
                    slots[slot] = state;
                }
            }
            return (derivesBase, slots.Values.Any(state => state.OverridesSdkSlot),
                implementsSpecializedContribution);

            string? BaseContributionSlot(MethodDefinition method)
            {
                if ((method.Attributes & MethodAttributes.Static) != 0) return null;
                var name = metadata.GetString(method.Name);
                if (name is not ("get_Panels" or "get_ComposerActions" or "CreateMessagePresentation"))
                    return null;
                var signature = method.DecodeSignature(MetadataSignatureTypeProvider.Instance, genericContext: null);
                if (!signature.Header.IsInstance || signature.GenericParameterCount != 0) return null;
                if (name == "get_Panels" && signature.ParameterTypes.Length == 0 &&
                    IsReadOnlyListOf(signature.ReturnType, "DesktopPanel")) return name;
                if (name == "get_ComposerActions" && signature.ParameterTypes.Length == 0 &&
                    IsReadOnlyListOf(signature.ReturnType, "DesktopAction")) return name;
                return name == "CreateMessagePresentation" && signature.ParameterTypes.Length == 1 &&
                       IsSdkShape(signature.ParameterTypes[0], "DesktopMessageSnapshot") &&
                       IsSdkShape(signature.ReturnType, "DesktopMessagePresentation") ? name : null;
            }

            string? ContributionDeclarationSlot(MemberReference declaration)
            {
                var name = metadata.GetString(declaration.Name);
                var signature = declaration.DecodeMethodSignature(MetadataSignatureTypeProvider.Instance, genericContext: null);
                // The SDK getter declaration has the default, nongeneric instance header.
                if (signature.Header.RawValue != 0x20 || signature.GenericParameterCount != 0 ||
                    signature.ParameterTypes.Length != 0 || !signature.ReturnType.IsTypeReference ||
                    signature.ReturnType.AssemblyName != "System.Runtime") return null;
                return name switch
                {
                    "get_Panels" when IsReadOnlyListOf(signature.ReturnType, "DesktopPanel") => name,
                    "get_ComposerActions" when IsReadOnlyListOf(signature.ReturnType, "DesktopAction") => name,
                    _ => null
                };
            }

            string? CovariantArrayContributionSlot(MethodDefinition method)
            {
                if ((method.Attributes & MethodAttributes.Static) != 0) return null;
                var name = metadata.GetString(method.Name);
                if (name is not ("get_Panels" or "get_ComposerActions")) return null;
                var signature = method.DecodeSignature(MetadataSignatureTypeProvider.Instance, genericContext: null);
                if (signature.Header.RawValue != 0x20 || signature.GenericParameterCount != 0 ||
                    signature.ParameterTypes.Length != 0 || signature.ReturnType.Name != "szarray" ||
                    signature.ReturnType.TypeArguments.Length != 1) return null;
                return IsSdkShape(signature.ReturnType.TypeArguments[0],
                    name == "get_Panels" ? "DesktopPanel" : "DesktopAction") ? name : null;
            }

            static bool IsReadOnlyListOf(MetadataTypeShape shape, string item) =>
                shape.Name == "System.Collections.Generic.IReadOnlyList`1" &&
                shape.TypeArguments.Length == 1 && IsSdkShape(shape.TypeArguments[0], item);

            static bool IsSdkShape(MetadataTypeShape shape, string name) =>
                shape.IsTypeReference && shape.Name == "Cerebral.Desktop.Plugins." + name &&
                shape.AssemblyName == "Cerebral.Desktop.Plugins" && shape.TypeArguments.IsEmpty;

            bool HasSpecializedContribution(EntityHandle handle)
            {
                if (IsSdkType(handle, "IDesktopMountPlugin") ||
                    IsSdkType(handle, "IDesktopConfigurationPlugin") ||
                    IsSdkType(handle, "IDesktopTextEntryPlugin") ||
                    IsSdkType(handle, "IDesktopTextEntrySubmissionPlugin") ||
                    IsSdkType(handle, "IDesktopInputModePlugin") ||
                    IsSdkType(handle, "IDesktopComposerCommandPlugin") ||
                    IsSdkType(handle, "IDesktopAgentTemplatePlugin") ||
                    IsSdkType(handle, "IDesktopConversationWorkspacePlugin") ||
                    IsSdkType(handle, "IDesktopConversationSettingsPlugin"))
                    return true;
                if (handle.Kind == HandleKind.TypeSpecification)
                    throw new InvalidDataException("The desktop entry uses a generic contribution interface whose SDK identity cannot be verified.");
                if (handle.Kind != HandleKind.TypeDefinition || !visitedInterfaces.Add(handle)) return false;
                if (visitedInterfaces.Count > maximumTypes)
                    throw new InvalidDataException("The desktop entry interface graph exceeds the metadata inspection limit.");
                var definition = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
                foreach (var implementationHandle in definition.GetInterfaceImplementations())
                    if (HasSpecializedContribution(metadata.GetInterfaceImplementation(implementationHandle).Interface))
                        return true;
                return false;
            }

            bool IsSdkType(EntityHandle handle, string name)
            {
                if (handle.Kind != HandleKind.TypeReference) return false;
                var reference = metadata.GetTypeReference((TypeReferenceHandle)handle);
                if (metadata.GetString(reference.Namespace) != "Cerebral.Desktop.Plugins" ||
                    metadata.GetString(reference.Name) != name ||
                    reference.ResolutionScope.Kind != HandleKind.AssemblyReference) return false;
                var assembly = metadata.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope);
                return metadata.GetString(assembly.Name) == "Cerebral.Desktop.Plugins";
            }

            bool IsSystemObject(EntityHandle handle)
            {
                if (handle.Kind != HandleKind.TypeReference) return false;
                var reference = metadata.GetTypeReference((TypeReferenceHandle)handle);
                if (metadata.GetString(reference.Namespace) != "System" ||
                    metadata.GetString(reference.Name) != "Object" ||
                    reference.ResolutionScope.Kind != HandleKind.AssemblyReference) return false;
                var assembly = metadata.GetString(metadata.GetAssemblyReference(
                    (AssemblyReferenceHandle)reference.ResolutionScope).Name);
                return assembly is "System.Runtime" or "System.Private.CoreLib" or "mscorlib";
            }

            string TypeName(TypeDefinitionHandle handle)
            {
                var names = new List<string>();
                var visited = new HashSet<TypeDefinitionHandle>();
                while (!handle.IsNil)
                {
                    if (names.Count == maximumTypes || !visited.Add(handle))
                        throw new InvalidDataException("The desktop assembly contains an invalid declaring-type chain.");
                    var definition = metadata.GetTypeDefinition(handle);
                    names.Add(metadata.GetString(definition.Name));
                    var parent = definition.GetDeclaringType();
                    if (parent.IsNil)
                    {
                        names.Reverse();
                        var name = string.Join("+", names);
                        var space = metadata.GetString(definition.Namespace);
                        return space.Length == 0 ? name : space + "." + name;
                    }
                    handle = parent;
                }
                throw new InvalidDataException("The desktop assembly contains an invalid declaring-type chain.");
            }
        }
        catch (Exception error) when (error is BadImageFormatException or ArgumentException)
        {
            throw new InvalidDataException("The desktop entry contribution metadata is malformed and cannot be verified safely.", error);
        }
    }

    private static (string Id, string Version, string Manifest, string Assembly)? VerifyService(string root)
    {
        var directory = Path.Combine(root, "Service");
        if (!Directory.Exists(directory)) return null;
        _ = SelectComponentProject(directory) ??
            throw new InvalidDataException("The service component has no project file. Exact-inspect this source root before building.");
        var manifestPath = Path.Combine(directory, "bin", "Release", "net10.0", "service-plugin.json");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("The service Release manifest is missing. Build the Service project before reporting success.", manifestPath);
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var value = document.RootElement;
        var id = RequiredManifestText(value, "id");
        var version = RequiredManifestText(value, "version");
        NativeDesktopPluginPackage.ValidateId(id);
        if (!Version.TryParse(version, out _)) throw new InvalidDataException("The service Release manifest version must be a valid System.Version value.");
        if (RequiredManifestText(value, "contract") != ServicePluginContract.Id ||
            value.GetProperty("apiVersion").GetInt32() != ServicePluginContract.Version)
            throw new InvalidDataException("The service Release manifest uses an unsupported contract version.");
        var assemblyName = RequiredManifestText(value, "assembly");
        if (Path.GetFileName(assemblyName) != assemblyName || !assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The service Release manifest assembly must be one DLL filename beside the manifest.");
        var assembly = Path.Combine(Path.GetDirectoryName(manifestPath)!, assemblyName);
        VerifyFreshOutput(directory, manifestPath, assembly, "service");
        return (id, version, manifestPath, assembly);
    }

    private static void VerifyFreshOutput(string sourceDirectory, string manifest, string assembly, string label,
        ToolExecutionContext? context = null)
    {
        if (!File.Exists(manifest)) throw new FileNotFoundException($"The {label} Release manifest is missing. Build that component before reporting success.", manifest);
        if (!File.Exists(assembly)) throw new FileNotFoundException($"The {label} Release assembly is missing. Build that component before reporting success.", assembly);
        var sourceManifest = Path.Combine(sourceDirectory, Path.GetFileName(manifest));
        if (context is not null) _ = ToolPolicy.PathInScope(sourceManifest, context, ScopedPathAccess.Read);
        if (!File.Exists(sourceManifest) || !File.ReadAllBytes(sourceManifest).AsSpan().SequenceEqual(File.ReadAllBytes(manifest)))
            throw new InvalidDataException($"The {label} Release manifest does not match its source manifest. Rebuild that component before reporting success.");
        var sourceFiles = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !PathComparer.Equals(path, sourceManifest))
            .Where(path => Path.GetExtension(path) is ".cs" or ".csproj" or ".json").ToArray();
        if (context is not null)
            foreach (var path in sourceFiles) _ = ToolPolicy.PathInScope(path, context, ScopedPathAccess.Read, mustExist: true);
        var latestSource = sourceFiles.Length == 0 ? DateTime.MinValue : sourceFiles.Max(File.GetLastWriteTimeUtc);
        // Exact bytes above prove the copied manifest, which can change without triggering compilation.
        // Keep the assembly timestamp fenced against the remaining code, project and JSON inputs.
        if (File.GetLastWriteTimeUtc(assembly) < latestSource)
            throw new InvalidDataException($"The {label} Release output predates edited source. Rebuild that component before reporting success.");
    }

    private static string RequiredManifestText(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())
            ? item.GetString()! : throw new InvalidDataException($"The service Release manifest requires nonempty '{name}'.");

    private static string NormalizeSdk(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(label + " plugin SDK binding must be an existing absolute DLL path.");
        return Path.GetFullPath(path);
    }

    private static string? NormalizeOptionalSdk(string? path, string label) =>
        string.IsNullOrWhiteSpace(path) ? null : NormalizeSdk(path, label);

    private static void ValidateIdentity(string pluginId, string name, string component)
    {
        NativeDesktopPluginPackage.ValidateId(pluginId);
        if (name.Length is < 1 or > 100 || string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
            throw new ArgumentException("Plugin names contain 1 through 100 visible characters.");
        if (component is not (DesktopComponent or ServiceComponent or CrossSurfaceComponent))
            throw new ArgumentException("Plugin component must be desktop, service, or cross-surface.");
    }

    private static string? ReadPurposeDescription(JsonElement arguments)
    {
        var values = arguments.EnumerateObject().Where(property => property.NameEquals("description")).ToArray();
        if (values.Length == 0) return null; // existing four-field calls remain valid
        if (values.Length != 1 || values[0].Value.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Description must be one explicit purpose-specific string when supplied.");
        var value = values[0].Value.GetString()!.Trim();
        if (value.Length is < 1 or > 2000 || value.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
            throw new ArgumentException("Description must contain 1 through 2000 visible single-line characters.");
        return value;
    }

    private static string PurposeDescription(string name, string component, string? description) =>
        description ?? component switch
        {
            DesktopComponent => $"Native desktop plugin for {name}.",
            ServiceComponent => $"Service plugin for {name}.",
            CrossSurfaceComponent => $"Desktop and service plugin for {name}.",
            _ => throw new ArgumentException("Unknown plugin component.")
        };

    // This injective mapping uses only the already validated immutable ID. Display-name edits
    // must not change assembly identity; existing sources are inspected, never retrofitted.
    private static string ComponentAssemblyName(string pluginId, string component) =>
        "Pegasus.Plugin." + pluginId + "." + (component == DesktopComponent ? "Desktop" : "Service");

    private static string ComponentProjectFile(string pluginId, string component) =>
        ComponentAssemblyName(pluginId, component) + ".csproj";

    private static string ProjectSource(string template, string pluginId, string name, string component, string? description) =>
        // Replace only tokens in the original template; human text may itself contain token-like text.
        System.Text.RegularExpressions.Regex.Replace(template,
            "__ASSEMBLY_NAME__|__PLUGIN_NAME_XML__|__PLUGIN_DESCRIPTION_XML__", match => ProjectLiteral(match.Value switch
            {
                "__ASSEMBLY_NAME__" => ComponentAssemblyName(pluginId, component),
                "__PLUGIN_NAME_XML__" => name,
                _ => PurposeDescription(name, component, description)
            }));

    private static string ProjectLiteral(string value)
    {
        // XML escaping alone still lets MSBuild interpret property/item/metadata expressions.
        // Percent-escape build syntax first so the compiled metadata retains the supplied text.
        var literal = System.Text.RegularExpressions.Regex.Replace(value, @"[%$@;()*?]",
            match => "%" + ((int)match.Value[0]).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        return System.Security.SecurityElement.Escape(literal)!;
    }

    private static string? SelectComponentProject(string directory)
    {
        // A component has one canonical build envelope. Never pick an alphabetically first
        // project when multiple files could build different package identities.
        ToolPolicy.RejectLinks(directory);
        using var projects = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).GetEnumerator();
        if (!projects.MoveNext()) return null;
        var project = projects.Current;
        ToolPolicy.RejectLinks(project);
        if (projects.MoveNext())
            throw new InvalidDataException("The plugin component has multiple project files. Keep one authoritative project; no build path was guessed.");
        return project;
    }

    private static bool HasScaffoldProjectMarker(string directory, string component)
    {
        if (!Directory.Exists(directory)) return false;
        var project = SelectComponentProject(directory);
        if (project is null) return false;
        var file = Path.GetFileName(project);
        return file == (component == DesktopComponent ? "Cerebral.GeneratedPlugin.Desktop.csproj" : "Cerebral.GeneratedPlugin.Service.csproj") ||
            file.StartsWith("Pegasus.Plugin.", StringComparison.Ordinal) &&
            file.EndsWith(component == DesktopComponent ? ".Desktop.csproj" : ".Service.csproj", StringComparison.Ordinal);
    }

    private void WriteAll(string root, string pluginId, string name, string component, string? description,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(Path.Combine(root, "NuGet.Config"), NuGetConfig);
        Write(Path.Combine(root, "PLUGIN.md"), Instructions(pluginId, name, component, description));
        if (component is DesktopComponent or CrossSurfaceComponent)
            WriteDesktop(root, pluginId, name, description);
        if (component is ServiceComponent or CrossSurfaceComponent)
            WriteService(root, pluginId, name, description);
    }

    private void WriteDesktop(string root, string pluginId, string name, string? description)
    {
        var directory = Path.Combine(root, "Desktop");
        var sdk = Path.Combine(root, "sdk", "desktop");
        Directory.CreateDirectory(directory); Directory.CreateDirectory(sdk);
        File.Copy(desktopSdkAssembly!, Path.Combine(sdk, "Cerebral.Desktop.Plugins.dll"), false);
        Write(Path.Combine(directory, ComponentProjectFile(pluginId, DesktopComponent)),
            ProjectSource(DesktopProject, pluginId, name, DesktopComponent, description));
        Write(Path.Combine(directory, "GeneratedDesktopPlugin.cs"), DesktopSource
            .Replace("__PLUGIN_ID__", CSharp(pluginId), StringComparison.Ordinal)
            .Replace("__PLUGIN_NAME__", CSharp(name), StringComparison.Ordinal));
        Write(Path.Combine(directory, NativeDesktopPluginPackage.ManifestFileName), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            contract = NativeDesktopPluginPackage.ContractId,
            apiVersion = NativeDesktopPluginPackage.ApiVersion,
            id = pluginId,
            name,
            version = "1.0.0",
            description = PurposeDescription(name, DesktopComponent, description),
            assembly = ComponentAssemblyName(pluginId, DesktopComponent) + ".dll",
            entryType = "Cerebral.GeneratedPlugin.GeneratedDesktopPlugin"
        }, new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = true }));
    }

    private void WriteService(string root, string pluginId, string name, string? description)
    {
        var directory = Path.Combine(root, "Service");
        var sdk = Path.Combine(root, "sdk", "service");
        Directory.CreateDirectory(directory); Directory.CreateDirectory(sdk);
        File.Copy(serviceSdkAssembly, Path.Combine(sdk, "Cerebral.Core.dll"), false);
        ServiceSdkReference.Write(root);
        Write(Path.Combine(directory, ComponentProjectFile(pluginId, ServiceComponent)),
            ProjectSource(ServiceProject, pluginId, name, ServiceComponent, description));
        Write(Path.Combine(directory, "GeneratedServicePlugin.cs"), ServiceSource
            .Replace("__PLUGIN_ID__", CSharp(pluginId), StringComparison.Ordinal)
            .Replace("__PLUGIN_NAME__", CSharp(name), StringComparison.Ordinal));
        Write(Path.Combine(directory, "service-plugin.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            contract = ServicePluginContract.Id,
            apiVersion = ServicePluginContract.Version,
            id = pluginId,
            name,
            version = "1.0.0",
            description = PurposeDescription(name, ServiceComponent, description),
            assembly = ComponentAssemblyName(pluginId, ServiceComponent) + ".dll",
            entryType = "Cerebral.GeneratedPlugin.GeneratedServicePlugin",
            dependencies = Array.Empty<object>()
        }, new JsonSerializerOptions(CerebralJson.Options) { WriteIndented = true }));
    }

    private static object Result(string root, string pluginId, string name, string component, string? description)
    {
        var desktop = component is DesktopComponent or CrossSurfaceComponent;
        var service = component is ServiceComponent or CrossSurfaceComponent;
        return new
        {
            pluginId,
            name,
            component,
            root,
            description = PurposeDescription(name, component, description),
            guide = Path.Combine(root, "PLUGIN.md"),
            entryContract = desktop ? DesktopBaseContract : null,
            desktopAuthoringContract = desktop ? ToolModelProjection.DesktopPluginAuthoringContractId : null,
            desktop = desktop ? new
            {
                source = Path.Combine(root, "Desktop", "GeneratedDesktopPlugin.cs"),
                project = Path.Combine(root, "Desktop", ComponentProjectFile(pluginId, DesktopComponent)),
                build = BuildSummary(Path.Combine(root, "Desktop"), Path.Combine(root, "Desktop", ComponentProjectFile(pluginId, DesktopComponent)), "desktop", "desktop-plugin.json"),
                builtManifest = Path.Combine(root, "Desktop", "bin", "Release", "net10.0-windows", "desktop-plugin.json")
            } : null,
            service = service ? new
            {
                source = Path.Combine(root, "Service", "GeneratedServicePlugin.cs"),
                sdkReference = ServiceSdkReference.Inspect(root),
                project = Path.Combine(root, "Service", ComponentProjectFile(pluginId, ServiceComponent)),
                build = BuildSummary(Path.Combine(root, "Service"), Path.Combine(root, "Service", ComponentProjectFile(pluginId, ServiceComponent)), "service", "service-plugin.json"),
                builtManifest = Path.Combine(root, "Service", "bin", "Release", "net10.0", "service-plugin.json")
            } : null,
            next = component switch
            {
                DesktopComponent => "Read PLUGIN.md and only relevant plugin.contract topics. Preserve the scaffold manifest entryType and exact entry class namespace/name; deliberately changing that type requires updating both source and source manifest, then rebuilding. Implement the requested desktop behavior, use the exact returned Release build coordinates, call plugin.verify on this root, then call plugin.present once with the exact built desktop manifest.",
                ServiceComponent => "Read Service/SERVICE-SDK.md for the selected copied SDK declarations and PLUGIN.md for lifecycle guidance, then implement the requested service behavior. Use the exact returned Release build coordinates, then call plugin.verify on this root. Report the verified service manifest for owner staging; do not claim it loaded.",
                CrossSurfaceComponent => "Read PLUGIN.md and only relevant plugin.contract topics. Implement only the requested behavior, build both exact returned Release envelopes, call plugin.verify on this root, then call plugin.present once with the exact built desktop manifest. Service lifecycle operations remain owner-controlled.",
                _ => throw new ArgumentException("Unknown plugin component.")
            }
        };
    }

    private static void Write(string path, string content) =>
        File.WriteAllText(path, content.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));

    private static string CSharp(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\u2028", "\\u2028", StringComparison.Ordinal)
        .Replace("\u2029", "\\u2029", StringComparison.Ordinal);

    private static string Instructions(string pluginId, string name, string component, string? description)
    {
        var shared = $$"""
            # {{name}}

            Package ID: `{{pluginId}}`  
            Components: `{{component}}`  
            Purpose: {{PurposeDescription(name, component, description)}}  
            Contract version: 1

            Preserve the admitted name and optional purpose description; do not infer a missing purpose. For example,
            "Metrics History" could describe "Record local metrics history and compare daily trends." Do not copy that
            example identity. The stable package ID fixes project/assembly filenames; changing display text must not
            rename an existing package or its data.

            This source was created by `plugin.scaffold` from the exact SDK assemblies under `sdk/`. Edit only source and
            source manifests; never edit `bin/` or `obj/` output. Before creating another project, call `plugin.projects`
            with `{}` for discovery. If a row matches, copy its absolute root verbatim and call `plugin.projects` again
            with `{ "root": "<exact returned root>" }` before reading, editing, building, verifying, or presenting it.
            Never substitute its ID, name, basename, component directory, manifest, or a guessed path.
            If discovery reports `limited` or the tool result is truncated, a missing row does not prove that no suitable
            source exists; do not scaffold from that absence.

            Use the exact project, working directory, arguments, and built manifest returned by `plugin.scaffold` or the
            exact-root `plugin.projects` response. Those are the canonical Release build coordinates; do not guess a
            project path or omit Release. Implement only the requested behavior. After the last source edit and exact
            Release build, call `plugin.verify` on this source root—the directory containing `PLUGIN.md`.
            """;

        var componentGuide = component switch
        {
            DesktopComponent => """
                ## Desktop

                This is a desktop-only project. Read only the `plugin.contract` topics needed for the requested behavior.
                Work only from the returned desktop source and build coordinates; do not add unrequested components. Keep
                the public parameterless constructor host-independent. If using
                `DesktopPluginBase`, use `Host` or `Environment` only after `base.Initialize(pluginHost)`. After verification
                succeeds, call `plugin.present` once with the exact built desktop manifest returned by the scaffold or
                inventory. Presentation queues owner review; it does not prove installation, activation, or rendering.
                """,
            ServiceComponent => """
                ## Service

                Read `Service/SERVICE-SDK.md` for the selected public declarations pinned to the copied service SDK.
                This reference is readable inside the Service component scope; it does not prove model consumption or behavior.
                This is a service-only project. Work only from the returned service source and build coordinates; do not add
                unrequested components. After verification succeeds, report the exact built service manifest for owner
                staging. Service staging, activation, restart, testing, deactivation, and removal remain owner-controlled
                Worker lifecycle operations; do not claim that the service loaded or ran.
                """,
            CrossSurfaceComponent => """
                ## Desktop and service

                Read only the `plugin.contract` topics needed for the requested desktop behavior.
                Build both returned component envelopes in Release, then verify this shared source root. After verification
                succeeds, call `plugin.present` once with the exact built desktop manifest. Keep the desktop public
                parameterless constructor host-independent; if using `DesktopPluginBase`, use `Host` or `Environment` only
                after `base.Initialize(pluginHost)`.
                Service staging, activation, restart, testing, deactivation, and removal remain owner-controlled Worker lifecycle operations; do not claim that the service loaded or ran.
                """,
            _ => throw new ArgumentException("Unknown plugin component.")
        };
        var coordinates = component switch
        {
            DesktopComponent => $"Project: Desktop/{ComponentProjectFile(pluginId, DesktopComponent)}; assembly: {ComponentAssemblyName(pluginId, DesktopComponent)}.dll",
            ServiceComponent => $"Project: Service/{ComponentProjectFile(pluginId, ServiceComponent)}; assembly: {ComponentAssemblyName(pluginId, ServiceComponent)}.dll",
            _ => $"Desktop project: Desktop/{ComponentProjectFile(pluginId, DesktopComponent)}; service project: Service/{ComponentProjectFile(pluginId, ServiceComponent)}."
        };
        return shared + "\n\n" + coordinates + "\n\n" + componentGuide;
    }

    private const string DesktopBaseContract = """
        // Optional minimal base. Override only requested contributions; do not redeclare unused ones.
        public abstract class DesktopPluginBase : IDesktopPlugin
        {
            protected IDesktopPluginHost Host { get; }
            protected IDesktopPluginEnvironment Environment { get; }
            public virtual void Initialize(IDesktopPluginHost pluginHost);
            public virtual IReadOnlyList<DesktopPanel> Panels { get; } // empty by default
            public virtual IReadOnlyList<DesktopAction> ComposerActions { get; } // empty by default
            public virtual DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message); // null
            public virtual void Dispose();
        }
        // Initialize/Dispose overrides must call base. Environment throws if the optional host capability is absent.
        // Direct implementations of the existing interface remain supported:
        public interface IDesktopPlugin : IDisposable
        {
            void Initialize(IDesktopPluginHost pluginHost);
            IReadOnlyList<DesktopPanel> Panels { get; }
            IReadOnlyList<DesktopAction> ComposerActions { get; }
            DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message);
        }
        // Namespace: Cerebral.Desktop.Plugins. FrameworkElement is System.Windows.FrameworkElement.
        public sealed record DesktopPanel(string Id, string Title, Func<FrameworkElement> CreateView);
        public sealed record DesktopAction(string Id, string Label,
            Func<CancellationToken, Task> ExecuteAsync, string? ToolTip = null);
        // Relevant host read member; do not redeclare the SDK interface in a plugin.
        public interface IDesktopPluginHost { DesktopDraftSnapshot GetDraft(); }
        public sealed record DesktopConversationBinding(string Generation, string? SessionId);
        public sealed record DesktopDraftSnapshot(DesktopConversationBinding Binding, long Revision, string Text);
        // A panel's factory returns ordinary WPF controls, e.g. System.Windows.Controls.StackPanel.
        // The host mounts that returned instance. Calling CreateView again constructs a detached
        // tree, never refreshes the displayed tree. Event handlers must update their captured controls
        // or real WPF bindings. Dispose cleans up resources; do not save stale or failed state there.
        // DesktopPanel has no HTML, Content, Position, Width, or Height members.
        """;

    private const string DesktopBaseExample = """
        using System.Windows;
        using System.Windows.Controls;
        using Cerebral.Desktop.Plugins;

        namespace Cerebral.GeneratedPlugin;

        public sealed class GeneratedDesktopPlugin : DesktopPluginBase
        {
            // Only for a requested sidebar. For other contributions keep Panels empty.
            public override IReadOnlyList<DesktopPanel> Panels => [new("main", "Example", CreateView)];
            private FrameworkElement CreateView()
            {
                var panel = new StackPanel { Margin = new Thickness(8) };
                var text = new TextBlock { Text = "Example", TextWrapping = TextWrapping.Wrap };
                text.SetResourceReference(TextBlock.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
                var update = new Button { Content = "Update", Margin = new Thickness(0, 8, 0, 0) };
                // This is the same TextBlock the host mounts, not a newly constructed view.
                update.Click += (_, _) => text.Text = "Updated";
                panel.Children.Add(text);
                panel.Children.Add(update);
                return panel;
            }
        }
        """;

    private const string MountViewContract = """
        public interface IDesktopMountPlugin
        {
            IReadOnlyList<IDesktopMountContribution> MountContributions { get; }
        }
        [Flags]
        public enum DesktopMountSurfaces
        {
            None = 0,
            Orchestrator = 1,
            Configuration = 2,
            All = Orchestrator | Configuration
        }
        public sealed record DesktopMountApplicability
        {
            public DesktopMountSurfaces Surfaces { get; init; } = DesktopMountSurfaces.All;
        }
        public sealed record DesktopMountDescriptor(string Id, string Slot)
        {
            public DesktopMountApplicability Applicability { get; init; } = new();
        }
        public sealed record DesktopMountView(DesktopMountDescriptor Descriptor, string Title,
            Func<FrameworkElement> CreateView) : IDesktopMountContribution;
        public static class DesktopMountSlots
        {
            public const string StatusBar = "status.bar";
            public const string NewConversationStart = "conversation.start";
        }
        """;

    private const string MountViewExample = """
        using System.Windows;
        using System.Windows.Controls;
        using Cerebral.Desktop.Plugins;

        namespace Cerebral.GeneratedPlugin;

        public sealed class GeneratedDesktopPlugin : IDesktopPlugin, IDesktopMountPlugin
        {
            public const string PluginId = "example.status";
            public const string PluginName = "Example Status";
            private IDesktopPluginHost? host;
            private TextBlock? status;

            public void Initialize(IDesktopPluginHost pluginHost)
            {
                host = pluginHost ?? throw new ArgumentNullException(nameof(pluginHost));
                host.ConversationChanged += OnConversationChanged;
                host.LeadMessageCommitted += OnLeadMessageCommitted;
            }

            public IReadOnlyList<DesktopPanel> Panels { get; } = [];
            public IReadOnlyList<DesktopAction> ComposerActions { get; } = [];
            public IReadOnlyList<IDesktopMountContribution> MountContributions =>
            [
                new DesktopMountView(
                    new DesktopMountDescriptor("conversation-status", DesktopMountSlots.StatusBar)
                    {
                        Applicability = new() { Surfaces = DesktopMountSurfaces.Orchestrator }
                    },
                    PluginName, CreateStatusView)
            ];

            public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;

            private FrameworkElement CreateStatusView()
            {
                status = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                Refresh();
                return status;
            }

            private void Refresh()
            {
                if (host is null || status is null) return;
                var conversation = host.GetConversation();
                var balance = (host as IDesktopConversationSettingsHost)?.GetResponseBalance();
                var share = balance?.RequestedSharePercent is int percent ? $"{percent}%" : "Agent default";
                status.Text = $"{conversation.Title} · {(conversation.IsRunning ? "Working" : "Idle")} · " +
                    (balance is null ? "Response unavailable" : $"{balance.ContextTokens:N0} context · {balance.EffectiveResponseTokens:N0} response · {share}");
            }

            private void OnConversationChanged(object? sender, DesktopConversationChangedEventArgs args) => Refresh();
            private void OnLeadMessageCommitted(object? sender, DesktopLeadMessageEventArgs args) => Refresh();

            public void Dispose()
            {
                if (host is not null)
                {
                    host.ConversationChanged -= OnConversationChanged;
                    host.LeadMessageCommitted -= OnLeadMessageCommitted;
                }
                host = null;
                status = null;
            }
        }
        """;

    private const string NewConversationContract = """
        public interface IDesktopMountPlugin
        {
            IReadOnlyList<IDesktopMountContribution> MountContributions { get; }
        }
        public sealed record DesktopMountDescriptor(string Id, string Slot);
        public sealed record DesktopMountView(DesktopMountDescriptor Descriptor, string Title,
            Func<FrameworkElement> CreateView) : IDesktopMountContribution;
        public static class DesktopMountSlots
        {
            public const string NewConversationStart = "conversation.start";
        }
        public sealed record DesktopDraftSnapshot(DesktopConversationBinding Binding, long Revision, string Text);
        public sealed record DesktopDraftUpdate(DesktopConversationBinding Binding, long ExpectedRevision, string Text);
        public interface IDesktopPluginHost
        {
            DesktopDraftSnapshot GetDraft();
            Task<DesktopOperationResult> ReplaceDraftAsync(DesktopDraftUpdate update,
                CancellationToken cancellationToken = default);
            void ReportStatus(string text);
        }
        """;

    private const string NewConversationExample = """
        using System.IO;
        using System.Windows;
        using System.Windows.Controls;
        using System.Windows.Media;
        using System.Windows.Media.Imaging;
        using Cerebral.Desktop.Plugins;

        namespace Cerebral.GeneratedPlugin;

        public sealed class GeneratedDesktopPlugin : IDesktopPlugin, IDesktopMountPlugin
        {
            public const string PluginId = "example.new-conversation";
            public const string PluginName = "Starting Points";
            private IDesktopPluginHost? host;

            public void Initialize(IDesktopPluginHost pluginHost) =>
                host = pluginHost ?? throw new ArgumentNullException(nameof(pluginHost));

            public IReadOnlyList<DesktopPanel> Panels { get; } = [];
            public IReadOnlyList<DesktopAction> ComposerActions { get; } = [];
            public IReadOnlyList<IDesktopMountContribution> MountContributions =>
            [
                new DesktopMountView(
                    new DesktopMountDescriptor("starting-points", DesktopMountSlots.NewConversationStart)
                    {
                        Order = 100,
                        Applicability = new() { Surfaces = DesktopMountSurfaces.Orchestrator }
                    },
                    PluginName,
                    CreateStartView)
            ];

            public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;

            private FrameworkElement CreateStartView()
            {
                var root = new Grid { MaxWidth = 920, Margin = new Thickness(24) };
                var watermark = TryWatermark(@"C:\branding\watermark.png");
                if (watermark is not null) root.Children.Add(watermark);

                var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                content.Children.Add(new TextBlock
                {
                    Text = "What would you like to create?",
                    FontSize = 28,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 8)
                });
                content.Children.Add(new TextBlock
                {
                    Text = "Choose a starting point, edit it in the composer, then send when ready.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.82,
                    Margin = new Thickness(0, 0, 0, 18)
                });

                var choices = new WrapPanel();
                choices.Children.Add(Starter("Plan a project", "Help me turn this idea into a practical project plan: "));
                choices.Children.Add(Starter("Explore an idea", "Help me explore this idea, including useful alternatives and tradeoffs: "));
                choices.Children.Add(Starter("Build something", "Help me design and build this, then verify that it works: "));
                content.Children.Add(choices);
                root.Children.Add(content);
                return root;
            }

            private Button Starter(string label, string prompt)
            {
                var button = new Button
                {
                    Content = label,
                    MinWidth = 150,
                    Margin = new Thickness(0, 0, 10, 10),
                    Padding = new Thickness(14, 8, 14, 8)
                };
                button.Click += async (_, _) => await PutInComposerAsync(prompt);
                return button;
            }

            private async Task PutInComposerAsync(string text)
            {
                if (host is null) return;
                var draft = host.GetDraft();
                var result = await host.ReplaceDraftAsync(new(draft.Binding, draft.Revision, text));
                host.ReportStatus(result.State is DesktopOperationState.Completed or DesktopOperationState.Accepted
                    ? "Starting point added. Edit it, then send when ready."
                    : result.Message ?? "The starting point could not be added.");
            }

            private static Image? TryWatermark(string path)
            {
                if (!File.Exists(path)) return null;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform,
                    Opacity = 0.08,
                    IsHitTestVisible = false
                };
            }

            public void Dispose() => host = null;
        }
        """;

    private const string StorageContract = """
        // Namespace: Cerebral.Desktop.Plugins
        public enum DesktopPluginSurface { Orchestrator, Configuration }
        public interface IDesktopPluginEnvironment
        {
            DesktopPluginSurface Surface { get; }
            string DataDirectory { get; }
            string PackageDirectory { get; }
        }
        // DesktopPluginBase already retains Environment; use Environment.DataDirectory in CreateView.
        // Only direct IDesktopPlugin implementations query pluginHost as IDesktopPluginEnvironment in Initialize.
        // Do not implement or redeclare this host interface in the plugin.
        public static class DesktopPluginFiles
        {
            public static string ReadUtf8(string path, int maximumBytes);
            public static void WriteUtf8Atomically(string path, string text, int maximumBytes);
        }
        // Missing file/directory throws FileNotFoundException/DirectoryNotFoundException.
        // Limits are 0–16 MiB of UTF-8 bytes. Read after creating the view's controls.
        """;

    private const string StorageExample = """
        // Optional C# members for your existing plugin class; include using System.IO.
        // DesktopPluginBase needs no new Initialize or environment field:
        // in CreateView, compose Path.Combine(Environment.DataDirectory, "settings.txt").
        // Only a direct IDesktopPlugin checks pluginHost as IDesktopPluginEnvironment in Initialize.
        // Optional wrappers around the actual SDK helpers; no extra interface or contribution is needed.
        // Qualify the SDK type: a same-named local helper in a reused project can have different behavior.
        // SDK saves create the parent directory and replace atomically; reads and writes expose errors.
        // Do not silently truncate data or turn failures into success/default values.
        private static void WritePrivateText(string path, string text, int maximumBytes)
            => global::Cerebral.Desktop.Plugins.DesktopPluginFiles.WriteUtf8Atomically(path, text, maximumBytes);

        private static string ReadPrivateText(string path, int maximumBytes)
            => global::Cerebral.Desktop.Plugins.DesktopPluginFiles.ReadUtf8(path, maximumBytes);
        """;

    private const string DraftContract = """
        // Namespace: Cerebral.Desktop.Plugins
        public sealed record DesktopConversationBinding(string Generation, string? SessionId);
        public sealed record DesktopDraftSnapshot(DesktopConversationBinding Binding, long Revision, string Text);
        // Relevant members only; do not redeclare SDK interfaces in a plugin.
        public interface IDesktopPluginHost
        {
            DesktopDraftSnapshot GetDraft();
        }
        """;

    private const string DraftExample = """
        using System.Windows;
        using System.Windows.Controls;
        using Cerebral.Desktop.Plugins;

        namespace Cerebral.GeneratedPlugin;

        public sealed class GeneratedDesktopPlugin : IDesktopPlugin
        {
            private IDesktopPluginHost? host;
            public void Initialize(IDesktopPluginHost pluginHost) =>
                host = pluginHost ?? throw new ArgumentNullException(nameof(pluginHost));
            public IReadOnlyList<DesktopPanel> Panels => [new("main", "Draft preview", CreateView)];
            public IReadOnlyList<DesktopAction> ComposerActions { get; } = [];
            public DesktopMessagePresentation? CreateMessagePresentation(DesktopMessageSnapshot message) => null;
            private FrameworkElement CreateView()
            {
                var view = new StackPanel { Margin = new Thickness(8) };
                var display = new TextBlock { TextWrapping = TextWrapping.Wrap };
                display.SetResourceReference(TextBlock.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
                var refresh = new Button { Content = "Refresh", Margin = new Thickness(0, 8, 0, 0) };
                void UpdateDisplay() => display.Text = host?.GetDraft().Text ?? string.Empty;
                refresh.Click += (_, _) => UpdateDisplay();
                view.Children.Add(display);
                view.Children.Add(refresh);
                UpdateDisplay();
                return view;
            }
            public void Dispose() => host = null;
        }
        """;

    private const string ConversationContract = """
        public sealed record DesktopConversationBinding(string Generation, string? SessionId);
        public sealed record DesktopConversationSnapshot(DesktopConversationBinding Binding, string Title,
            string? ProjectId, string? ProjectPath, bool IsRunning);
        public interface IDesktopPluginHost
        {
            DesktopConversationSnapshot GetConversation();
            event EventHandler<DesktopConversationChangedEventArgs>? ConversationChanged;
            event EventHandler<DesktopLeadMessageEventArgs>? LeadMessageCommitted;
        }
        """;

    private const string ConversationExample = """
        var conversation = host.GetConversation();
        var title = conversation.Title;
        var state = conversation.IsRunning ? "Working" : "Idle";
        """;

    private const string ConversationSettingsContract = """
        // Namespace: Cerebral.Desktop.Plugins. This optional contract is independent of package API v1.
        public static class DesktopConversationSettingsContract
        {
            public const string Id = "pegasus.cerebral.desktop.conversation-settings";
            public const int Version = 1;
            public const int MaximumContributionsPerPlugin = 8;
            public const int MaximumContributionIdChars = 64;
            public const int MaximumTitleChars = 100;
            public const int MaximumStatusChars = 2_000;
        }
        public sealed record DesktopConversationSettingsContext(string ConversationId, string Title,
            string ProfileId, string? ProjectId, string? WorkingDirectory, CancellationToken AvailabilityToken);
        public sealed record DesktopConversationSettingsContribution(string Id, string Title,
            Func<DesktopConversationSettingsContext, CancellationToken, Task<IDesktopConversationSettingsEditor>> OpenAsync)
        {
            public int ContractVersion { get; init; } = DesktopConversationSettingsContract.Version;
        }
        public interface IDesktopConversationSettingsPlugin
        {
            IReadOnlyList<DesktopConversationSettingsContribution> ConversationSettings { get; }
        }
        public interface IDesktopConversationSettingsEditor : IDisposable
        {
            FrameworkElement View { get; }
            Task<DesktopOperationResult> SaveAsync(CancellationToken cancellationToken = default);
            void Cancel();
        }
        // Existing operation result; do not redeclare this SDK type in plugin code.
        public enum DesktopOperationState { Accepted, Completed, Rejected, Failed, Uncertain }
        public sealed record DesktopOperationResult(DesktopOperationState State, string? Message = null,
            string? RequestId = null, string? JobId = null);
        """;

    private const string ConversationSettingsExample = """
        using System.IO;
        using System.Security.Cryptography;
        using System.Text;
        using System.Text.Json;
        using System.Windows;
        using System.Windows.Controls;
        using Cerebral.Desktop.Plugins;

        namespace Cerebral.GeneratedPlugin;

        // Local-only example. A plugin serving multiple Worker owners needs its own verified owner storage scope.
        public sealed class GeneratedDesktopPlugin : DesktopPluginBase, IDesktopConversationSettingsPlugin
        {
            public IReadOnlyList<DesktopConversationSettingsContribution> ConversationSettings =>
                [new("note", "Conversation note", OpenNoteAsync)];

            private Task<IDesktopConversationSettingsEditor> OpenNoteAsync(
                DesktopConversationSettingsContext context, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                context.AvailabilityToken.ThrowIfCancellationRequested();
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context.ConversationId)));
                var path = Path.Combine(Environment.DataDirectory, "conversation-notes", key + ".txt");
                return Task.FromResult<IDesktopConversationSettingsEditor>(new NoteEditor(context, path));
            }

            private sealed class NoteEditor : IDesktopConversationSettingsEditor
            {
                private const int MaximumBytes = 4096;
                private readonly DesktopConversationSettingsContext context;
                private readonly string path;
                private readonly string original;
                private readonly TextBox note;
                private bool disposed;
                public FrameworkElement View { get; }

                public NoteEditor(DesktopConversationSettingsContext context, string path)
                {
                    this.context = context; this.path = path;
                    original = File.Exists(path) ? DesktopPluginFiles.ReadUtf8(path, MaximumBytes) : "";
                    note = new TextBox { Text = original, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                        MaxLength = MaximumBytes, MinHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                    note.SetResourceReference(Control.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
                    var target = new TextBlock { Text = "Note for " + context.Title, TextWrapping = TextWrapping.Wrap };
                    target.SetResourceReference(TextBlock.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
                    var panel = new StackPanel(); panel.Children.Add(target); panel.Children.Add(note);
                    View = panel; // This new element has no parent until this editor is mounted.
                }

                public Task<DesktopOperationResult> SaveAsync(CancellationToken cancellationToken = default)
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.AvailabilityToken);
                    linked.Token.ThrowIfCancellationRequested();
                    if (disposed) return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected, "This editor is closed."));
                    if (Encoding.UTF8.GetByteCount(note.Text) > MaximumBytes)
                        return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Rejected, "The note exceeds 4096 UTF-8 bytes."));
                    try
                    {
                        DesktopPluginFiles.WriteUtf8Atomically(path, note.Text, MaximumBytes);
                        return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed, "Conversation note saved."));
                    }
                    catch (IOException)
                    {
                        return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Uncertain,
                            "Save was not confirmed. Reopen the editor to read the saved note before another save."));
                    }
                }

                public void Cancel() { if (!disposed) note.Text = original; } // Discards only this local draft.
                public void Dispose() { if (disposed) return; disposed = true; note.IsEnabled = false; }
            }
        }
        """;

    private const string SettingsNavigationContract = """
        // Namespace: Cerebral.Desktop.Plugins. These are existing additive API-v1 types.
        public sealed record DesktopSettingsNavigationAvailability(bool Available, string? Message = null);
        public interface IDesktopPluginSettingsNavigationHost
        {
            DesktopSettingsNavigationAvailability GetSettingsNavigationAvailability(string panelId);
            Task<DesktopOperationResult> OpenSettingsAsync(string panelId, CancellationToken cancellationToken = default);
        }
        public interface IDesktopConfigurationPlugin
        {
            IReadOnlyList<DesktopPanel> ConfigurationPanels { get; }
        }
        // Existing SDK result types; do not redeclare them in plugin code.
        public enum DesktopOperationState { Accepted, Completed, Rejected, Failed, Uncertain }
        public sealed record DesktopOperationResult(DesktopOperationState State, string? Message = null,
            string? RequestId = null, string? JobId = null);
        """;

    private const string SettingsNavigationExample = """
        using System.Windows;
        using System.Windows.Controls;
        using Cerebral.Desktop.Plugins;

        namespace Cerebral.GeneratedPlugin;

        public sealed class GeneratedDesktopPlugin : DesktopPluginBase, IDesktopConfigurationPlugin
        {
            private const string SettingsPanelId = "settings";

            // The host scopes this panel to this plugin's ID and loaded package generation.
            public IReadOnlyList<DesktopPanel> ConfigurationPanels =>
                [new(SettingsPanelId, "Example settings", CreateSettingsView)];
            public override IReadOnlyList<DesktopAction> ComposerActions =>
                [new("open-settings", "Settings", OpenSettingsAsync)];

            private static FrameworkElement CreateSettingsView() =>
                new TextBlock { Text = "Example settings", Margin = new Thickness(8) };

            private async Task OpenSettingsAsync(CancellationToken token)
            {
                var host = Host;
                if (host is not IDesktopPluginSettingsNavigationHost navigation)
                {
                    ReportIfActive(host, "Open Configuration to view this plugin's settings.");
                    return;
                }

                try
                {
                    var availability = navigation.GetSettingsNavigationAvailability(SettingsPanelId);
                    if (!availability.Available)
                    {
                        ReportIfActive(host, availability.Message ?? "Plugin settings are unavailable for this connection.");
                        return;
                    }
                    var result = await navigation.OpenSettingsAsync(SettingsPanelId, token);
                    ReportIfActive(host, result.State switch
                    {
                        DesktopOperationState.Completed => "The matching Configuration panel is visible and ready.",
                        DesktopOperationState.Rejected => result.Message ?? "Settings navigation was refused.",
                        _ => "Settings navigation was not confirmed. Inspect Configuration before trying again."
                    });
                }
                catch (OperationCanceledException)
                {
                    // Cancellation after invocation does not prove that Configuration stayed closed.
                    ReportIfActive(host, "Settings navigation may have taken effect. Inspect Configuration before trying again.");
                }
                catch (ObjectDisposedException)
                {
                    // A retiring scoped host cannot confirm whether the receiver opened the panel.
                    ReportIfActive(host, "Settings navigation may have taken effect. Inspect Configuration before trying again.");
                }
            }

            private static void ReportIfActive(IDesktopPluginHost host, string message)
            {
                try { host.ReportStatus(message); }
                catch (ObjectDisposedException) { } // The retired host has no status surface to update.
            }
        }
        """;

    private const string ResponseSettingsContract = """
        public sealed record DesktopResponseBalanceSnapshot(DesktopConversationBinding Binding,
            int? RequestedSharePercent, int ContextTokens, int EffectiveResponseTokens);
        public interface IDesktopConversationSettingsHost
        {
            DesktopResponseBalanceSnapshot GetResponseBalance();
            Task<DesktopOperationResult> SetResponseBalanceAsync(DesktopConversationBinding expectedBinding,
                int? responseSharePercent, CancellationToken cancellationToken = default);
        }
        """;

    private const string ResponseSettingsExample = """
        if (host is IDesktopConversationSettingsHost settings)
        {
            var balance = settings.GetResponseBalance();
            var requested = balance.RequestedSharePercent?.ToString() ?? "Agent default";
        }
        """;

    private const string ComposerCommandContract = """
        public interface IDesktopComposerCommandPlugin
        {
            IReadOnlyList<DesktopComposerCommand> ComposerCommands { get; }
        }
        public sealed record DesktopComposerCommand(string Id,
            Func<DesktopInputRequest, CancellationToken, Task<DesktopComposerCommandResult>> ExecuteAsync);
        public sealed record DesktopInputRequest(DesktopConversationSnapshot Conversation,
            DesktopSubmission Submission, bool IsSteering);
        public enum DesktopComposerCommandState { Pass, Continue, Handled }
        public sealed record DesktopComposerCommandResult(DesktopComposerCommandState State,
            int ConsumedPrefixCharacters = 0, string? Message = null);
        """;

    private const string ComposerCommandExample = """
        public IReadOnlyList<DesktopComposerCommand> ComposerCommands { get; }
        public GeneratedDesktopPlugin() => ComposerCommands = [new("example", ExecuteAsync)];
        private static Task<DesktopComposerCommandResult> ExecuteAsync(DesktopInputRequest input, CancellationToken token) =>
            Task.FromResult(new DesktopComposerCommandResult(DesktopComposerCommandState.Pass));
        """;

    private const string NuGetConfig = """
        <configuration>
          <packageSources><clear /></packageSources>
        </configuration>
        """;

    private const string DesktopProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0-windows</TargetFramework>
            <UseWPF>true</UseWPF>
            <EnableWindowsTargeting>true</EnableWindowsTargeting>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <AssemblyName>__ASSEMBLY_NAME__</AssemblyName>
            <AssemblyTitle>__PLUGIN_NAME_XML__</AssemblyTitle>
            <Description>__PLUGIN_DESCRIPTION_XML__</Description>
            <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
            <RestoreSources></RestoreSources>
          </PropertyGroup>
          <ItemGroup>
            <Reference Include="Cerebral.Desktop.Plugins">
              <HintPath>../sdk/desktop/Cerebral.Desktop.Plugins.dll</HintPath>
              <Private>false</Private>
            </Reference>
            <None Update="desktop-plugin.json" CopyToOutputDirectory="Always" />
          </ItemGroup>
          <Target Name="RequireDesktopPluginManifest" BeforeTargets="PrepareForBuild">
            <Error Condition="!Exists('desktop-plugin.json')" Text="The required source desktop-plugin.json is missing. Restore the source manifest before building." />
          </Target>
        </Project>
        """;

    private const string ServiceProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <AssemblyName>__ASSEMBLY_NAME__</AssemblyName>
            <AssemblyTitle>__PLUGIN_NAME_XML__</AssemblyTitle>
            <Description>__PLUGIN_DESCRIPTION_XML__</Description>
            <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
            <RestoreSources></RestoreSources>
          </PropertyGroup>
          <ItemGroup>
            <Reference Include="Cerebral.Core">
              <HintPath>../sdk/service/Cerebral.Core.dll</HintPath>
              <Private>false</Private>
            </Reference>
            <None Update="service-plugin.json" CopyToOutputDirectory="Always" />
          </ItemGroup>
          <Target Name="RequireServicePluginManifest" BeforeTargets="PrepareForBuild">
            <Error Condition="!Exists('service-plugin.json')" Text="The required source service-plugin.json is missing. Restore the source manifest before building." />
          </Target>
        </Project>
        """;

    private const string DesktopSource = """
        using Cerebral.Desktop.Plugins;

        namespace Cerebral.GeneratedPlugin;

        public sealed class GeneratedDesktopPlugin : DesktopPluginBase
        {
            public const string PluginId = "__PLUGIN_ID__";
            public const string PluginName = "__PLUGIN_NAME__";
        }
        """;

    private sealed record MetadataTypeShape(string Name, string? AssemblyName,
        ImmutableArray<MetadataTypeShape> TypeArguments, bool IsTypeReference = false);

    private sealed class MetadataSignatureTypeProvider : ISignatureTypeProvider<MetadataTypeShape, object?>
    {
        public static MetadataSignatureTypeProvider Instance { get; } = new();
        private static readonly ImmutableArray<MetadataTypeShape> NoTypes = ImmutableArray<MetadataTypeShape>.Empty;

        public MetadataTypeShape GetArrayType(MetadataTypeShape elementType, ArrayShape shape) =>
            new("array:" + shape.Rank, null, ImmutableArray.Create(elementType));
        public MetadataTypeShape GetByReferenceType(MetadataTypeShape elementType) =>
            new("byref", null, ImmutableArray.Create(elementType));
        public MetadataTypeShape GetFunctionPointerType(MethodSignature<MetadataTypeShape> signature) =>
            new("function-pointer", null, NoTypes);
        public MetadataTypeShape GetGenericInstantiation(MetadataTypeShape genericType,
            ImmutableArray<MetadataTypeShape> typeArguments) => genericType with { TypeArguments = typeArguments };
        public MetadataTypeShape GetGenericMethodParameter(object? genericContext, int index) =>
            new("!!" + index, null, NoTypes);
        public MetadataTypeShape GetGenericTypeParameter(object? genericContext, int index) =>
            new("!" + index, null, NoTypes);
        public MetadataTypeShape GetModifiedType(MetadataTypeShape modifier, MetadataTypeShape unmodifiedType,
            bool isRequired) => new(isRequired ? "modreq" : "modopt", null,
                ImmutableArray.Create(modifier, unmodifiedType));
        public MetadataTypeShape GetPinnedType(MetadataTypeShape elementType) =>
            new("pinned", null, ImmutableArray.Create(elementType));
        public MetadataTypeShape GetPointerType(MetadataTypeShape elementType) =>
            new("pointer", null, ImmutableArray.Create(elementType));
        public MetadataTypeShape GetPrimitiveType(PrimitiveTypeCode typeCode) =>
            new("primitive:" + typeCode, null, NoTypes);
        public MetadataTypeShape GetSZArrayType(MetadataTypeShape elementType) =>
            new("szarray", null, ImmutableArray.Create(elementType));

        public MetadataTypeShape GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle,
            byte rawTypeKind)
        {
            var type = reader.GetTypeDefinition(handle);
            var space = reader.GetString(type.Namespace);
            var name = reader.GetString(type.Name);
            var assembly = reader.GetString(reader.GetAssemblyDefinition().Name);
            return new(space.Length == 0 ? name : space + "." + name, assembly, NoTypes);
        }

        public MetadataTypeShape GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle,
            byte rawTypeKind)
        {
            var type = reader.GetTypeReference(handle);
            var space = reader.GetString(type.Namespace);
            var name = reader.GetString(type.Name);
            var scope = type.ResolutionScope;
            // Proof shapes are top-level SDK/framework types. A nested or cyclic
            // TypeRef scope cannot establish that identity and is never traversed.
            if (scope.Kind != HandleKind.AssemblyReference)
                return new("unresolved-type-reference", null, NoTypes);
            var assembly = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
            return new(space.Length == 0 ? name : space + "." + name, assembly, NoTypes, IsTypeReference: true);
        }

        public MetadataTypeShape GetTypeFromSpecification(MetadataReader reader, object? genericContext,
            TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }

    private sealed record InventoryAdmission(IReadOnlyList<string> Roots);
    private sealed class InventoryScanState
    {
        public InventoryScanState(string[] roots) { Roots = roots; }
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Token { get; private set; } = Guid.NewGuid().ToString("N");
        public void RotateToken() => Token = Guid.NewGuid().ToString("N");
        public DateTimeOffset CreatedUtc { get; } = DateTimeOffset.UtcNow;
        public string[] Roots { get; }
        public Queue<string> Pending { get; } = new();
        public HashSet<string> Queued { get; } = new(PathComparer);
        public HashSet<string> Seen { get; } = new(PathComparer);
        public List<PluginProjectSummary> Projects { get; } = [];
        public int ScannedDirectories { get; set; }
        public int Pages { get; set; }
        public string? IncompleteReason { get; private set; }
        public void MarkIncomplete(string message) => IncompleteReason ??= message;
    }
    private sealed record PluginProjectInventory(IReadOnlyList<PluginProjectSummary> Projects, IReadOnlyList<string> Roots,
        int ScannedDirectories, bool Limited, string? IncompleteReason)
    {
        public string? Continuation { get; init; }
        public int? TotalProjectsFound { get; init; }
        public int? TotalDirectoriesScanned { get; init; }
        public string? InventoryId { get; init; }
        public int? InventoryPage { get; init; }
        public bool TraversalComplete { get; init; }
        public IReadOnlyList<PluginProjectSummary>? AllProjects { get; init; }
    }
    private static StringComparer PathComparer => ToolPolicy.PathComparison == StringComparison.OrdinalIgnoreCase
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record PluginBuildSummary(string ExecutableId, IReadOnlyList<string> Arguments,
        string WorkingDirectory, string BuiltManifest);
    private sealed record PluginComponentSummary(string Component, string Manifest, string? Source, PluginBuildSummary? Build,
        string? Id, string? Name, string? Version, string? Error)
    {
        public ServiceSdkReferenceSummary? SdkReference { get; init; }
    }
    private sealed record PluginProjectSummary(string Root, string? Id, string? Name, string? Version,
        IReadOnlyList<PluginComponentSummary> Components, string? Error);

    private const string ServiceSource = """
        // Read SERVICE-SDK.md for selected declarations pinned to this copied SDK before editing.
        using System.Text.Json;
        using Cerebral.Core;

        namespace Cerebral.GeneratedPlugin;

        public sealed class GeneratedServicePlugin : IServicePlugin
        {
            public const string PluginId = "__PLUGIN_ID__";
            public const string PluginName = "__PLUGIN_NAME__";
            public const string ServiceMessage = "Generated service ready";

            public void Initialize(IServicePluginHost host)
            {
                if (host.PluginId != PluginId) throw new InvalidOperationException("The host plugin identity does not match this package.");
            }

            public IReadOnlyList<IToolExtension> ToolExtensions { get; } = [new StatusTool()];
            public IReadOnlyList<IServiceCommandExtension> CommandExtensions { get; } = [new StatusCommand()];
            public void Dispose() { }

            private static object Status() => new { pluginId = PluginId, version = "1.0.0", message = ServiceMessage };

            private sealed class StatusCommand : IServiceCommandExtension
            {
                public IReadOnlyList<string> Operations { get; } = [PluginId + ".status"];
                public Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (operation != PluginId + ".status" || arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any())
                        throw new ArgumentException("The status command accepts one empty object.");
                    return Task.FromResult<object?>(Status());
                }
            }

            private sealed class StatusTool : IToolExtension
            {
                public IReadOnlyList<ToolDescriptor> Tools { get; } =
                [
                    new(PluginId + ".agent-status", "Read the generated plugin's bounded service status.",
                        CerebralJson.Element(new { type = "object", properties = new { }, additionalProperties = false }),
                        "diagnostics")
                ];

                public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (call.Name != PluginId + ".agent-status" || call.Arguments.ValueKind != JsonValueKind.Object || call.Arguments.EnumerateObject().Any())
                        throw new ArgumentException("The status tool accepts one empty object.");
                    return Task.FromResult(new ToolResult(true, JsonSerializer.Serialize(Status(), CerebralJson.Options))
                    {
                        Evidence = new(ToolExecutionOutcome.Succeeded, PluginId, State: "ready")
                    });
                }
            }
        }
        """;
}
