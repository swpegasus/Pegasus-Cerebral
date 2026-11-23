using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Frozen project scope and reference routing with actual snapshot/effect admission and
/// a typed fixture dispatcher. No provider connection, model inference or runtime startup.</summary>
internal static class WorkflowReferenceRoutingTests
{
    private const string ProjectId = "reference-project";
    private const string ProjectSpace = "project.reference-routing";
    private const string SharedSpace = "knowledge.shared";
    private const string AddedSpace = "project.later-routing";
    private const string SearchTool = "mcp.cortex.cortex_search";
    private const string ReadTool = "mcp.cortex.cortex_read";
    private const string PrivateMarker = "FIXTURE_PRIVATE_SOURCE_AND_PROVIDER_VALUE";

    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "cerebral-reference-routing-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        bool Rejects(Action action)
        {
            try { action(); return false; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
            { return true; }
        }
        try
        {
            var state = new StateStore(Path.Combine(root, "state"));
            var configuration = new Configuration(state);
            var events = new EventHub();
            void RestoreHost()
            {
                var settings = configuration.Get();
                settings.CortexNamespaces = [ProjectSpace, SharedSpace, AddedSpace];
                settings.CortexWriteNamespaces = [ProjectSpace, AddedSpace];
                settings.McpServers =
                [
                    new() { Id = "cortex", Executable = Environment.ProcessPath!, Enabled = true,
                        Arguments = [PrivateMarker] },
                    new() { Id = "other-cortex", Executable = Environment.ProcessPath!, Enabled = true }
                ];
                configuration.Save(settings);
            }
            RestoreHost();
            state.Put("navigation", "sidebar", new NavigationState
            {
                Nodes =
                [
                    new() { Id = ProjectId, Kind = "project", Title = "Reference fixture", Path = root },
                    new() { Id = "foreign-project", Kind = "project", Title = "Foreign fixture", Path = root },
                    new() { Id = "unconfigured-project", Kind = "project", Title = "Unconfigured fixture", Path = root }
                ]
            });
            void ProjectSettings(IReadOnlyList<string> reads, string? write = ProjectSpace,
                bool requireApproval = true, string server = "cortex")
            {
                var revision = state.Get<ProjectKnowledgeSettings>("project-knowledge", ProjectId)?.Revision ?? 0;
                state.Put("project-knowledge", ProjectId, new ProjectKnowledgeSettings
                {
                    ProjectId = ProjectId, Revision = revision + 1, CortexServerId = server,
                    ReadNamespaces = reads.ToList(), WriteNamespace = write,
                    RequireKnowledgeApproval = requireApproval, AutomaticRetrieval = false, AutomaticContinuity = false
                });
            }
            ProjectSettings([ProjectSpace, SharedSpace]);
            var projects = new ProjectKnowledge(state, configuration, events);
            var profile = new AgentProfile
            {
                Id = "reference-lead", Name = "Reference fixture", Role = "lead", Revision = 1,
                ModelId = "reference-fixture", FollowsDefaultModel = false, FollowsModelBudgets = false,
                ContextTokens = 8192, ResponseTokens = 1024, AutomaticCompaction = false,
                MaxSteps = 12, MaxSeconds = 30, AllowedRoots = [root], AllowedTools = ["*"],
                Capabilities = ["mcp.cortex", "mcp.other-cortex", "filesystem.read"]
            };
            state.Put("agent", profile.Id, profile);
            state.Put("model", profile.ModelId, new ModelRecord
            {
                Id = profile.ModelId, Name = "Never loaded reference fixture", Path = Path.Combine(root, "not-loaded.gguf"),
                AutomaticContextSizing = false, ContextTokens = 8192, MaximumContextTokens = 8192,
                RecommendedContextTokens = 8192
            });
            var tools = new RecordingTools();
            var captured = await WorkerExecutionSnapshot.CaptureAsync(state, profile, configuration, tools, [], default,
                projectId: ProjectId);
            var snapshot = WorkerExecutionSnapshot.Read(captured);
            var accepted = snapshot.CortexScope!;
            Check(accepted.ProjectId == ProjectId && accepted.ServerId == "cortex" &&
                accepted.ReadNamespaces.SequenceEqual([ProjectSpace, SharedSpace]) &&
                accepted.WriteNamespace == ProjectSpace && accepted.RequireKnowledgeApproval &&
                !accepted.KnowledgeWriteApproved &&
                accepted.ProviderFingerprint == CortexOperationPolicy.ProviderFingerprint(configuration.Get().McpServers[0]),
                "Admission freezes the exact project, provider identity, read/write scope and unapproved policy.");
            Check(tools.Executions.Count == 0, "Scope capture does not execute a semantic tool or contact a provider.");
            tools.BeforeList = () => ProjectSettings([ProjectSpace], write: null);
            var admissionRaceRejected = false;
            try
            {
                await WorkerExecutionSnapshot.CaptureAsync(state, profile, configuration, tools, [], default,
                    projectId: ProjectId);
            }
            catch (InvalidOperationException)
            {
                admissionRaceRejected = true;
            }
            Check(admissionRaceRejected && tools.Executions.Count == 0,
                "Project scope changes during descriptor discovery reject snapshot admission without executing work.");
            ProjectSettings([ProjectSpace, SharedSpace]);

            CortexAccessScope Effective(WorkerExecutionSnapshot.Snapshot? selected = null, string? project = ProjectId) =>
                WorkflowReferenceRouting.EffectiveCortexScope(state, configuration, selected ?? snapshot, project)!;
            var effective = Effective();
            Check(WorkflowReferenceRouting.SameScope(accepted, effective), "Unchanged configuration retains the accepted scope.");

            ProjectSettings([ProjectSpace, SharedSpace, AddedSpace], AddedSpace, requireApproval: false);
            effective = Effective();
            Check(effective.ReadNamespaces.SequenceEqual([ProjectSpace, SharedSpace]) &&
                effective.WriteNamespace is null && effective.RequireKnowledgeApproval && !effective.KnowledgeWriteApproved,
                "Later project expansion cannot widen accepted reads, substitute a write target or relax accepted approval.");
            Check(snapshot.CortexScope!.ReadNamespaces.SequenceEqual([ProjectSpace, SharedSpace]) &&
                snapshot.CortexScope.WriteNamespace == ProjectSpace,
                "Scope intersection leaves the accepted snapshot unchanged.");
            var tightenedHost = configuration.Get();
            tightenedHost.CortexNamespaces = [ProjectSpace, AddedSpace];
            tightenedHost.CortexWriteNamespaces = [AddedSpace];
            configuration.Save(tightenedHost);
            effective = Effective();
            Check(effective.ReadNamespaces.SequenceEqual([ProjectSpace]) && effective.WriteNamespace is null,
                "Current host namespace and write revocations narrow accepted project execution.");
            RestoreHost();
            ProjectSettings([SharedSpace], write: null, requireApproval: true);
            effective = Effective();
            Check(effective.ReadNamespaces.SequenceEqual([SharedSpace]) && effective.WriteNamespace is null,
                "Current project read/write revocations also narrow the admitted scope.");
            var formerlyUnrestricted = snapshot with
            {
                CortexScope = accepted with { RequireKnowledgeApproval = false, KnowledgeWriteApproved = true }
            };
            Check(Effective(formerlyUnrestricted).RequireKnowledgeApproval &&
                !Effective(formerlyUnrestricted).KnowledgeWriteApproved,
                "Current approval policy can tighten a snapshot and an accepted approval cannot manufacture current approval.");

            ProjectSettings([ProjectSpace, SharedSpace]);
            Check(Rejects(() => Effective(snapshot with { CortexScope = null })),
                "An old project snapshot missing scope fails closed instead of inheriting current host namespaces.");
            Check(Rejects(() => Effective(project: "foreign-project")) &&
                Rejects(() => Effective(snapshot with { CortexScope = accepted with { ProjectId = "foreign-project" } })),
                "Both a foreign submission project and a foreign saved scope are rejected.");
            Check(Rejects(() => Effective(snapshot with { CortexScope = accepted with { ServerId = "other-cortex" } })),
                "A foreign saved provider cannot borrow the current project's connection.");
            ProjectSettings([ProjectSpace, SharedSpace], server: "other-cortex");
            Check(Rejects(() => Effective()), "A current project provider substitution requires a fresh accepted request.");
            ProjectSettings([ProjectSpace, SharedSpace]);
            var changedProvider = configuration.Get();
            changedProvider.McpServers[0].Arguments.Add("changed-provider-identity");
            configuration.Save(changedProvider);
            Check(Rejects(() => Effective()), "A current provider identity change rejects the previously accepted binding.");
            RestoreHost();
            var disabledProvider = configuration.Get();
            disabledProvider.McpServers[0].Enabled = false;
            configuration.Save(disabledProvider);
            Check(Rejects(() => Effective()), "A disabled current provider cannot retain its accepted execution binding.");
            RestoreHost();

            var unbound = snapshot with { CortexScope = null };
            Check(WorkflowReferenceRouting.EffectiveCortexScope(state, configuration, unbound, null) is null &&
                Rejects(() => WorkflowReferenceRouting.EffectiveCortexScope(state, configuration, snapshot, null)),
                "Only a genuinely unbound snapshot can use the null project route.");
            state.Put("project-knowledge", "unconfigured-project", new ProjectKnowledgeSettings
                { ProjectId = "unconfigured-project", Revision = 0 });
            var unavailable = projects.ScopeFor("unconfigured-project");
            Check(unavailable.ServerId.Length == 0 && unavailable.ProviderFingerprint == "unavailable" &&
                unavailable.ReadNamespaces.Count == 0 && unavailable.WriteNamespace is null,
                "An unconfigured project has an explicit empty unavailable scope despite enabled host providers.");
            var unavailableSnapshot = WorkerExecutionSnapshot.Read(await WorkerExecutionSnapshot.CaptureAsync(
                state, profile, configuration, tools, [], default, projectId: "unconfigured-project"));
            var unavailableEffective = WorkflowReferenceRouting.EffectiveCortexScope(state, configuration,
                unavailableSnapshot, "unconfigured-project")!;
            Check(WorkflowReferenceRouting.SameScope(unavailable, unavailableEffective),
                "An unconfigured project's admission and execution preserve its explicit denial scope.");

            const string absentProject = "lineage-without-sidebar";
            var absent = projects.ScopeFor(absentProject, approvedKnowledgeWrite: true);
            Check(absent.ProjectId == absentProject && absent.ServerId.Length == 0 &&
                absent.ProviderFingerprint == "unavailable" && absent.ReadNamespaces.Count == 0 &&
                absent.WriteNamespace is null && absent.RequireKnowledgeApproval && !absent.KnowledgeWriteApproved,
                "Absent sidebar lineage has an explicit empty denial scope even when the caller requests write approval.");
            var absentSnapshot = WorkerExecutionSnapshot.Read(await WorkerExecutionSnapshot.CaptureAsync(
                state, profile, configuration, tools, [], default, projectId: absentProject));
            var absentEffective = WorkflowReferenceRouting.EffectiveCortexScope(state, configuration,
                absentSnapshot, absentProject)!;
            var absentRoutes = WorkflowReferenceRouting.ModelRoutes(absentSnapshot.Tools, profile, configuration, absentEffective);
            Check(WorkflowReferenceRouting.SameScope(absent, absentEffective) &&
                !absentRoutes.Contains(SearchTool, StringComparison.Ordinal) &&
                !absentRoutes.Contains(ReadTool, StringComparison.Ordinal) &&
                !absentRoutes.Contains("Knowledge.Cerebral.Guide", StringComparison.Ordinal),
                "Absent-sidebar admission and execution retain denial and never advertise the wider enabled host route.");
            Check(state.Get<ProjectKnowledgeSettings>("project-knowledge", absentProject) is null &&
                !state.Get<NavigationState>("navigation", "sidebar")!.Nodes.Any(node => node.Id == absentProject),
                "Resolving absent project lineage creates no inferred project configuration or sidebar node.");
            var savedNavigation = CerebralJson.Element(state.Get<NavigationState>("navigation", "sidebar")!);
            var missingNavigation = CerebralJson.Read<NavigationState>(savedNavigation);
            missingNavigation.Nodes.RemoveAll(node => node.Id == ProjectId);
            state.Put("navigation", "sidebar", missingNavigation);
            Check(Rejects(() => Effective()),
                "Removing a previously bound sidebar project invalidates its accepted Cortex access despite retained settings.");
            state.Put("navigation", "sidebar", CerebralJson.Read<NavigationState>(savedNavigation));

            effective = Effective();
            var routes = WorkflowReferenceRouting.ModelRoutes(snapshot.Tools, profile, configuration, effective);
            Check(Encoding.UTF8.GetByteCount(routes) <= 3072 &&
                routes.Contains(SearchTool, StringComparison.Ordinal) && routes.Contains(ReadTool, StringComparison.Ordinal) &&
                routes.Contains("plugin.projects", StringComparison.Ordinal) &&
                routes.Contains("plugin.contract", StringComparison.Ordinal) &&
                routes.Contains("receipt", StringComparison.Ordinal) && routes.Contains("history", StringComparison.Ordinal) &&
                routes.Contains("Knowledge.Cerebral.Guide", StringComparison.Ordinal),
                "The short route exposes exact admitted lookup names, guide coordinates, SDK and workflow evidence routes.");
            Check(!routes.Contains("mcp.other-cortex.", StringComparison.Ordinal) &&
                !routes.Contains("cortex_knowledge", StringComparison.Ordinal) &&
                !routes.Contains(PrivateMarker, StringComparison.Ordinal) &&
                !routes.Contains(root, StringComparison.Ordinal) &&
                !routes.Contains("privateSchema", StringComparison.Ordinal),
                "Routes exclude foreign providers, mutation routes, private descriptors, source paths and provider arguments.");
            var narrowed = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
            narrowed.AllowedTools = [SearchTool, "plugin.projects"];
            var narrowedRoutes = WorkflowReferenceRouting.ModelRoutes(snapshot.Tools, narrowed, configuration, effective);
            Check(narrowedRoutes.Contains(SearchTool, StringComparison.Ordinal) &&
                !narrowedRoutes.Contains(ReadTool, StringComparison.Ordinal) &&
                !narrowedRoutes.Contains("plugin.contract", StringComparison.Ordinal),
                "A lookup name absent from exact current tool grants is omitted.");
            narrowed.Capabilities = ["filesystem.read"];
            var revokedRoutes = WorkflowReferenceRouting.ModelRoutes(snapshot.Tools, narrowed, configuration, effective);
            Check(!revokedRoutes.Contains(SearchTool, StringComparison.Ordinal) &&
                !revokedRoutes.Contains(ReadTool, StringComparison.Ordinal) &&
                !revokedRoutes.Contains("Knowledge.Cerebral.Guide", StringComparison.Ordinal),
                "A revoked provider capability removes its lookup and guide-catalog routes.");
            var noAcceptedRead = snapshot with { Tools = snapshot.Tools.Where(tool => tool.Name != ReadTool).ToArray() };
            Check(!WorkflowReferenceRouting.ModelRoutes(noAcceptedRead.Tools, profile, configuration, effective)
                .Contains(ReadTool, StringComparison.Ordinal),
                "A tool newly available in the dispatcher cannot enter a snapshot that never accepted it.");
            Check(!WorkflowReferenceRouting.ModelRoutes(unavailableSnapshot.Tools, profile, configuration, unavailableEffective)
                .Contains(SearchTool, StringComparison.Ordinal),
                "Unavailable project scope never falls back to an enabled host lookup provider.");
            var noShared = effective with { ReadNamespaces = [ProjectSpace] };
            Check(!WorkflowReferenceRouting.ModelRoutes(snapshot.Tools, profile, configuration, noShared)
                .Contains("Knowledge.Cerebral.Guide", StringComparison.Ordinal),
                "The candidate knowledge catalog is omitted when its namespace is outside the effective read scope.");
            var disabled = configuration.Get();
            disabled.McpServers[0].Enabled = false;
            configuration.Save(disabled);
            Check(!WorkflowReferenceRouting.ModelRoutes(snapshot.Tools, profile, configuration, effective)
                .Contains(SearchTool, StringComparison.Ordinal),
                "Unavailable configured providers do not appear in reference routes.");
            RestoreHost();
            var configurationNeeded = configuration.Get();
            configurationNeeded.McpServers[0].RequiresConfiguration = true;
            configuration.Save(configurationNeeded);
            Check(!WorkflowReferenceRouting.ModelRoutes(snapshot.Tools, profile, configuration, effective)
                .Contains(SearchTool, StringComparison.Ordinal),
                "A provider requiring configuration is omitted rather than advertised as an available lookup.");
            RestoreHost();

            var unrelatedFirst = configuration.Get();
            unrelatedFirst.McpServers = Enumerable.Range(0, 8).Select(index => new McpRegistration
                { Id = "unrelated-" + index, Executable = Environment.ProcessPath!, Enabled = true })
                .Concat(unrelatedFirst.McpServers).ToList();
            configuration.Save(unrelatedFirst);
            var laterLookup = WorkflowReferenceRouting.ModelRoutes(unbound.Tools, profile, configuration, null);
            Check(laterLookup.Contains(SearchTool, StringComparison.Ordinal) && laterLookup.Contains(ReadTool, StringComparison.Ordinal),
                "Eight unrelated providers cannot consume the lookup budget before an exact-granted Cortex provider.");
            using (var laterDocument = JsonDocument.Parse(laterLookup[laterLookup.IndexOf('{')..]))
                Check(!laterDocument.RootElement.GetProperty("limited").GetBoolean() &&
                    !laterLookup.Contains("unrelated-", StringComparison.Ordinal),
                    "Unrelated providers neither appear as lookup routes nor falsely mark useful routes limited.");
            RestoreHost();

            void CheckLimitedCoordinates(string projection, IReadOnlyList<string> admittedNamespaces,
                IReadOnlyList<string> admittedToolNames)
            {
                Check(Encoding.UTF8.GetByteCount(projection) <= 3072,
                    "Adversarial lookup coordinates stay within 3072 UTF-8 bytes.");
                var start = projection.IndexOf('{');
                Check(start >= 0, "Bounded routing retains a complete structured metadata object.");
                using var document = JsonDocument.Parse(projection[start..]);
                var data = document.RootElement;
                Check(data.GetProperty("limited").GetBoolean(),
                    "Omitted lookup coordinates carry an explicit limited marker.");
                var projectedProviders = data.GetProperty("cortex").EnumerateArray().ToArray();
                Check(projectedProviders.Length is > 0 and <= 8,
                    "The bounded map retains useful complete routes and at most eight provider coordinates.");
                foreach (var provider in projectedProviders)
                {
                    var spaces = provider.GetProperty("readNamespaces").EnumerateArray()
                        .Select(value => value.GetString()!).ToArray();
                    Check(spaces.Length is > 0 and <= 8 &&
                        spaces.All(space => admittedNamespaces.Contains(space, StringComparer.Ordinal)),
                        "Every retained namespace is a complete admitted coordinate, never a clipped identifier.");
                    foreach (var name in new[] { "search", "read" })
                    {
                        var tool = provider.GetProperty(name);
                        Check(tool.ValueKind == JsonValueKind.Null || tool.ValueKind == JsonValueKind.String &&
                            admittedToolNames.Contains(tool.GetString()!, StringComparer.Ordinal),
                            "Every retained lookup name is a complete exact granted tool coordinate.");
                    }
                }
            }

            var crowdedNamespaces = Enumerable.Range(0, 25).Select(index => "project.crowded-" + index).ToArray();
            var crowdedProviders = Enumerable.Range(0, 17).Select(index => "routing-" + index).ToArray();
            var crowdedNames = crowdedProviders.SelectMany(provider => new[]
                { "mcp." + provider + ".cortex_search", "mcp." + provider + ".cortex_read" }).ToArray();
            var crowdedDescriptors = crowdedProviders.SelectMany(provider => new[]
            {
                new ToolDescriptor("mcp." + provider + ".cortex_search", PrivateMarker,
                    WorkflowContract.EmptyObject, "mcp." + provider),
                new ToolDescriptor("mcp." + provider + ".cortex_read", PrivateMarker,
                    WorkflowContract.EmptyObject, "mcp." + provider)
            }).ToArray();
            var crowdedProfile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile));
            crowdedProfile.Capabilities = crowdedProviders.Select(provider => "mcp." + provider).ToList();
            var crowdedHost = configuration.Get();
            crowdedHost.CortexNamespaces = crowdedNamespaces.ToList();
            crowdedHost.CortexWriteNamespaces = [];
            crowdedHost.McpServers = crowdedProviders.Select(provider => new McpRegistration
                { Id = provider, Executable = Environment.ProcessPath!, Enabled = true }).ToList();
            configuration.Save(crowdedHost);
            var crowdedSnapshot = snapshot with { CortexScope = null, Tools = crowdedDescriptors };
            var crowdedRoutes = WorkflowReferenceRouting.ModelRoutes(crowdedSnapshot.Tools, crowdedProfile, configuration, null);
            CheckLimitedCoordinates(crowdedRoutes, crowdedNamespaces, crowdedNames);
            Check(!crowdedRoutes.Contains(PrivateMarker, StringComparison.Ordinal),
                "Cardinality limits do not substitute full descriptor payloads for lookup coordinates.");
            RestoreHost();

            var oversizedNamespace = "project." + new string('\u03BB', 1024) + "-exact-tail";
            var longNamespaces = new[] { ProjectSpace, oversizedNamespace, SharedSpace };
            var longHost = configuration.Get();
            longHost.CortexNamespaces = longNamespaces.ToList();
            configuration.Save(longHost);
            var longRoutes = WorkflowReferenceRouting.ModelRoutes(unbound.Tools, profile, configuration, null);
            CheckLimitedCoordinates(longRoutes, longNamespaces,
                [SearchTool, ReadTool, "mcp.other-cortex.cortex_search", "mcp.other-cortex.cortex_read"]);
            using (var longDocument = JsonDocument.Parse(longRoutes[longRoutes.IndexOf('{')..]))
                Check(!longDocument.RootElement.GetProperty("cortex").EnumerateArray()
                    .SelectMany(provider => provider.GetProperty("readNamespaces").EnumerateArray())
                    .Any(value => value.GetString() == oversizedNamespace),
                    "A namespace too large for the complete coordinate budget is omitted in full.");
            RestoreHost();

            // Exercise production generation shaping with a capture-only adapter. It deliberately
            // stops at preparation; no model runtime, tokenization or inference is invoked.
            var probe = new PreparationProbe();
            var generationEffects = new WorkerWorkflowEffects(state, configuration, probe, tools);
            var generate = generationEffects.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.GenerateCommand && command.Descriptor.Version == 1);
            var routeSubmission = new WorkflowSubmission("generation-route-request", "generation-route-conversation", profile.Id,
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Use only the tools selected for this generation.", null, WorkflowContract.EmptyObject, captured)
                { ProjectId = ProjectId };
            async Task<JsonElement> GenerationRoutes(IReadOnlyList<string>? selected)
            {
                probe.Last = null;
                var arguments = CerebralJson.Element(new WorkerWorkflowEffects.GenerateArguments([], ToolNames: selected));
                var generated = await generate.ExecuteAsync(new("generation-route-effect", routeSubmission, arguments)
                    { RunId = "generation-route-run" }, default);
                Check(generated.State == WorkflowEffectState.Failed && probe.Last is not null,
                    "Capture-only preparation receives the real shaped request and stops before inference.");
                var route = probe.Last!.Messages.SelectMany(message => message.Content).OfType<TextContentPart>()
                    .Single(part => part.Text.StartsWith("Host-provided lookup coordinates", StringComparison.Ordinal)).Text;
                return JsonDocument.Parse(route[route.IndexOf('{')..]).RootElement.Clone();
            }
            var emptySelection = await GenerationRoutes([]);
            Check(emptySelection.GetProperty("cortex").GetArrayLength() == 0 &&
                emptySelection.GetProperty("guideCatalog").ValueKind == JsonValueKind.Null &&
                emptySelection.GetProperty("pluginProjects").ValueKind == JsonValueKind.Null &&
                emptySelection.GetProperty("pluginContract").ValueKind == JsonValueKind.Null,
                "An explicitly empty generation catalog advertises no Cortex or plugin lookup tools.");
            var sdkOnly = await GenerationRoutes(["plugin.contract"]);
            Check(sdkOnly.GetProperty("cortex").GetArrayLength() == 0 &&
                sdkOnly.GetProperty("guideCatalog").ValueKind == JsonValueKind.Null &&
                sdkOnly.GetProperty("pluginProjects").ValueKind == JsonValueKind.Null &&
                sdkOnly.GetProperty("pluginContract").GetString() == "plugin.contract",
                "A programming-style SDK-only selection does not advertise broader accepted Cortex or project tools.");
            var readOnly = await GenerationRoutes([ReadTool]);
            var readRoute = readOnly.GetProperty("cortex").EnumerateArray().Single();
            Check(readRoute.GetProperty("search").ValueKind == JsonValueKind.Null &&
                readRoute.GetProperty("read").GetString() == ReadTool &&
                readOnly.GetProperty("guideCatalog").GetString() == "Knowledge.Cerebral.Guide" &&
                readOnly.GetProperty("pluginContract").ValueKind == JsonValueKind.Null,
                "Partial selection advertises only its exact Cortex read route and reachable guide namespace.");
            var searchOnly = await GenerationRoutes([SearchTool]);
            var searchRoute = searchOnly.GetProperty("cortex").EnumerateArray().Single();
            Check(searchRoute.GetProperty("search").GetString() == SearchTool &&
                searchRoute.GetProperty("read").ValueKind == JsonValueKind.Null,
                "A search-only generation never advertises an unselected exact-read route.");
            var defaultSelection = await GenerationRoutes(null);
            Check(defaultSelection.GetProperty("cortex").GetArrayLength() == 1 &&
                defaultSelection.GetProperty("pluginProjects").GetString() == "plugin.projects" &&
                defaultSelection.GetProperty("pluginContract").GetString() == "plugin.contract",
                "An omitted selection retains the actually permitted default generation catalog.");
            var preparations = probe.Preparations;
            probe.Last = null;
            var forbiddenSelection = await generate.ExecuteAsync(new("generation-route-denied", routeSubmission,
                CerebralJson.Element(new WorkerWorkflowEffects.GenerateArguments([], ToolNames: ["unaccepted.reference"])))
                { RunId = "generation-route-run" }, default);
            Check(forbiddenSelection.State == WorkflowEffectState.Failed && probe.Last is null && probe.Preparations == preparations,
                "An unaccepted generation tool is rejected before preparation; routing never manufactures grants.");

            // Exercise the real host ToolCommand. The fixture changes project scope while discovery
            // runs, so the dispatcher must receive the newly narrowed intersection at dispatch.
            var models = new UnusedModels();
            var effects = new WorkerWorkflowEffects(state, configuration, models, tools);
            var handler = effects.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.ToolCommand);
            var submission = new WorkflowSubmission("reference-request", "reference-conversation", profile.Id,
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Inspect only the accepted project references.", null, WorkflowContract.EmptyObject, captured)
                { ProjectId = ProjectId };
            var args = CerebralJson.Element(new { name = SearchTool, arguments = new { }, workingDirectory = root });
            WorkflowSchema.Validate(args, handler.Descriptor.InputSchema);
            tools.BeforeList = () => ProjectSettings([ProjectSpace], write: null);
            var context = new WorkflowCommandContext("reference-effect", submission, args) { RunId = "reference-run" };
            var result = await handler.ExecuteAsync(context, default);
            Check(result.State == WorkflowEffectState.Succeeded && tools.Executions.Count == 1,
                "Actual host command admission dispatches the granted semantic lookup once.");
            var execution = tools.Executions.Single().Context;
            Check(execution.Cortex is { } forwarded && forwarded.ProjectId == ProjectId && forwarded.ServerId == "cortex" &&
                forwarded.ReadNamespaces.SequenceEqual([ProjectSpace]) && forwarded.WriteNamespace is null &&
                forwarded.RequireKnowledgeApproval && !forwarded.KnowledgeWriteApproved &&
                forwarded.ProviderFingerprint == accepted.ProviderFingerprint &&
                execution.SessionId == submission.ConversationId && execution.JobId == submission.RequestId &&
                execution.EffectId == context.EffectId && execution.EnforceProfileScope,
                "ToolCommand forwards narrowed project scope, current approval and exact host execution provenance.");
            var missingScope = submission with { SettingsSnapshot = CerebralJson.Element(snapshot with { CortexScope = null }) };
            var failed = await handler.ExecuteAsync(context with { Submission = missingScope }, default);
            Check(failed.State == WorkflowEffectState.Failed && tools.Executions.Count == 1 &&
                !string.IsNullOrWhiteSpace(failed.Error),
                "Old missing-scope project requests fail before the actual dispatcher is called.");
            Check(models.Preparations == 0,
                "These routing and tool admission checks perform no model preparation or inference.");
            Console.WriteLine($"Workflow reference routing checks passed: {checks} (snapshot/effect admission; mocked tools, no provider or model runtime).");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(root).StartsWith("cerebral-reference-routing-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe reference-routing fixture cleanup target.");
            Directory.Delete(root, true);
        }
    }

    private sealed class RecordingTools : IToolDispatcher
    {
        public bool FullAccessActive => false;
        public Action? BeforeList;
        public List<(ToolCall Call, ToolExecutionContext Context)> Executions { get; } = [];
        private static ToolDescriptor Descriptor(string name, string capability) => new(name,
            PrivateMarker + new string('x', 4096), CerebralJson.Element(new
            {
                type = "object", properties = new { privateSchema = new { type = "string" } }
            }), capability);
        private static readonly ToolDescriptor[] Descriptors =
        [
            Descriptor(SearchTool, "mcp.cortex"), Descriptor(ReadTool, "mcp.cortex"),
            Descriptor("mcp.cortex.cortex_status", "mcp.cortex"),
            Descriptor("mcp.cortex.cortex_knowledge", "mcp.cortex"),
            Descriptor("mcp.other-cortex.cortex_search", "mcp.other-cortex"),
            Descriptor("mcp.other-cortex.cortex_read", "mcp.other-cortex"),
            Descriptor("plugin.projects", "filesystem.read"), Descriptor("plugin.contract", "filesystem.read")
        ];
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var before = BeforeList; BeforeList = null; before?.Invoke();
            return Task.FromResult<IReadOnlyList<ToolDescriptor>>(Descriptors);
        }
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Executions.Add((call, context));
            return Task.FromResult(new ToolResult(true, "{}")
                { Evidence = new(ToolExecutionOutcome.Succeeded, "routing-fixture") });
        }
    }

    private sealed class PreparationProbe : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("routing-preparation-probe", "1.0.0", ["text"]);
        public ModelGenerationRequest? Last;
        public int Preparations;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Last = request;
            Preparations++;
            throw new InvalidOperationException("Capture-only fixture stops before inference.");
        }
    }

    private sealed class UnusedModels : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("routing-model-fixture", "1.0.0", ["text"]);
        public int Preparations;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            Preparations++;
            throw new InvalidOperationException("Reference routing must not start model preparation or inference.");
        }
    }
}
