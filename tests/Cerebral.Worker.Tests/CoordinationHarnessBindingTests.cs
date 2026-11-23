using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Activation;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Actual engine, command admission and SQLite with a trusted deterministic plugin-shaped
/// workflow and deterministic Conversation generation. No compiler, network or live model inference.</summary>
internal static class CoordinationHarnessBindingTests
{
    private const string ProjectId = "binding-owner-project";
    private const string SettingsKey = "cerebral.coordination";

    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-coordination-binding-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        try
        {
            var state = new StateStore(root);
            var config = new Configuration(state);
            var events = new EventHub();
            var settings = config.Get();
            settings.QueueCapacity = 100;
            config.Save(settings);
            state.Put("model", "binding-fixture", new ModelRecord
            {
                Id = "binding-fixture", Name = "Deterministic binding fixture",
                Path = Path.Combine(root, "not-loaded.gguf"), AutomaticContextSizing = false,
                ContextTokens = 8192, MaximumContextTokens = 8192, RecommendedContextTokens = 8192
            });
            foreach (var (id, role) in new[] { ("binding-lead", "lead"), ("legacy", "helper"), ("programmer", "helper") })
                state.Put("agent", id, new AgentProfile
                {
                    Id = id, Name = id, Purpose = "Independent bounded binding fixture", Role = role,
                    Revision = 1, Instructions = "Return only this role's bounded fixture evidence.",
                    ModelId = "binding-fixture", FollowsDefaultModel = false, FollowsModelBudgets = false,
                    AutomaticCompaction = false, ContextTokens = 8192, ResponseTokens = 1024,
                    MaxSteps = 24, MaxSeconds = 60, AllowedRoots = [root], AllowedTools = [],
                    Capabilities = ["agents.delegate", "agents.collaborate", "filesystem.read", "filesystem.write", "process.execute"]
                });
            var engineRoot = Path.Combine(root, "Engines");
            EngineHostTests.Pack(engineRoot, "A", true);
            await using var engine = new EngineController(engineRoot, Path.Combine(root, "EngineState"));
            await engine.InitializeAsync();
            var models = new DeterministicModels();
            var tools = new EmptyTools();
            var plugin = new ProgrammingFixtureModule();
            WorkerWorkflows? host = null;
            var effects = new WorkerWorkflowEffects(state, config, models, tools, workflowHost: () => host!);
            host = new WorkerWorkflows(state, config, events,
                [new ConversationWorkflowModule(), effects, plugin],
                [ConversationWorkflowModule.PackageBinding,
                    new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, []),
                    new(ProgrammingFixtureModule.ModuleId, ProgrammingFixtureDefinition.Version, true, [],
                        Generation: "binding-fixture-generation")],
                tools, engine: engine);

            var acceptedInput = CerebralJson.Element(new { rootPath = root, projectFile = "fixture.csproj" });
            JsonElement Binding(JsonElement input, string definitionId = ProgrammingFixtureDefinition.Id,
                string definitionVersion = ProgrammingFixtureDefinition.Version, int schemaVersion = 1) =>
                CerebralJson.Element(new { schemaVersion, definitionId, definitionVersion, input });
            void SetBinding(JsonElement? binding, bool hasRequiredGrants = true)
            {
                var profile = state.Get<AgentProfile>("agent", "programmer")!;
                profile.Revision++;
                profile.PluginSettings.Clear();
                if (binding is { } value) profile.PluginSettings[SettingsKey] = value.Clone();
                profile.Capabilities = hasRequiredGrants
                    ? ["agents.delegate", "agents.collaborate", "filesystem.read", "filesystem.write", "process.execute"]
                    : ["agents.delegate", "agents.collaborate"];
                state.Put("agent", profile.Id, profile);
            }
            JsonElement Assignments() => CerebralJson.Element(new
            {
                assignments = new[]
                {
                    new { profileId = "legacy", profileRevision = state.Get<AgentProfile>("agent", "legacy")!.Revision,
                        instruction = "Return LEGACY_EVIDENCE without the parent's private context." },
                    new { profileId = "programmer", profileRevision = state.Get<AgentProfile>("agent", "programmer")!.Revision,
                        instruction = "Return only the configured plugin's deterministic evidence." }
                }
            });
            async Task<Invocation> Invoke(string id, string commandId, JsonElement arguments, int commandVersion = 1)
            {
                var parent = await host.SubmitAsync(new(id, id + "-context", "binding-lead",
                    ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                    "PARENT_ONLY_PRIVATE. Coordinate the bounded independent assignments.",
                    null, CerebralJson.Element(new { projectId = ProjectId })), null, default);
                var handler = effects.Commands.Single(command => command.Descriptor.Id == commandId && command.Descriptor.Version == commandVersion);
                WorkflowSchema.Validate(arguments, handler.Descriptor.InputSchema);
                var claimed = host.Store.TryClaim(parent.Id)!;
                var prepared = host.Store.ApplyStep(parent.Id, claimed.Revision,
                    new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand(commandId, handler.Descriptor.Version, arguments)),
                    handler.Descriptor);
                var dispatched = host.Store.DispatchEffect(parent.Id, prepared.Revision);
                var context = new WorkflowCommandContext(dispatched.Effect!.Id, dispatched.Submission, arguments)
                    { RunId = parent.Id };
                return new(dispatched, handler, context, await handler.ExecuteAsync(context, default));
            }
            async Task CheckUnavailableCatalog(string id)
            {
                var catalog = await Invoke(id, WorkerWorkflowEffects.CatalogCommand, WorkflowContract.EmptyObject);
                Check(catalog.Result.State == WorkflowEffectState.Succeeded &&
                    catalog.Result.Data!.Value.GetProperty("helpers").EnumerateArray()
                        .Any(item => item.GetProperty("id").GetString() == "legacy") &&
                    !catalog.Result.Data.Value.GetProperty("helpers").EnumerateArray()
                        .Any(item => item.GetProperty("id").GetString() == "programmer"),
                    "An invalid optional helper is omitted without breaking ordinary helper discovery: " + id + ".");
                host.Store.CompleteEffect(catalog.Parent.Id, catalog.Context.EffectId, catalog.Result);
                host.Store.Cancel(catalog.Parent.Id);
            }
            async Task RejectBinding(string id, JsonElement binding, bool hasRequiredGrants = true, bool verifyCatalog = false)
            {
                SetBinding(binding, hasRequiredGrants);
                // The valid legacy helper is first. A bad second binding must be rejected before
                // admission begins, rather than leaving an avoidable partially admitted wave.
                var wave = await Invoke("reject-" + id, WorkerWorkflowEffects.DelegateCommand, Assignments());
                Check(wave.Result.State == WorkflowEffectState.Failed && !string.IsNullOrWhiteSpace(wave.Result.Error),
                    "Invalid helper binding is a visible ordinary rejection: " + id + ". " + wave.Result.Error);
                Check(host.Store.ChildRunIds(wave.Parent.Id).Count == 0,
                    "The complete wave is validated before any child is admitted: " + id + ".");
                host.Store.CompleteEffect(wave.Parent.Id, wave.Context.EffectId, wave.Result);
                host.Store.Cancel(wave.Parent.Id);
                if (verifyCatalog) await CheckUnavailableCatalog("invalid-binding-" + id + "-catalog");
            }

            await RejectBinding("unsupported-schema", Binding(acceptedInput, schemaVersion: 2));
            await RejectBinding("unknown-definition", Binding(acceptedInput, definitionId: "fixture.programming:missing"));
            await RejectBinding("unknown-version", Binding(acceptedInput, definitionVersion: "9.0.0"));
            await RejectBinding("invalid-input", Binding(CerebralJson.Element(new { rootPath = root })));
            await RejectBinding("extra-input-field", Binding(CerebralJson.Element(new
                { rootPath = root, projectFile = "fixture.csproj", projectId = "other-project" })));
            await RejectBinding("missing-grants", Binding(acceptedInput), hasRequiredGrants: false);
            await RejectBinding("recursive-delegation-grant", Binding(acceptedInput,
                definitionId: ProgrammingFixtureDefinition.DelegateId), verifyCatalog: true);
            await RejectBinding("recursive-collaboration-grant", Binding(acceptedInput,
                definitionId: ProgrammingFixtureDefinition.CollaborateId), verifyCatalog: true);
            await RejectBinding("project-substitution", Binding(CerebralJson.Element(new { projectId = "other-project" }),
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion));
            await RejectBinding("extra-setting-field", CerebralJson.Element(new
            {
                schemaVersion = 1, definitionId = ProgrammingFixtureDefinition.Id,
                definitionVersion = ProgrammingFixtureDefinition.Version, input = acceptedInput, unexpected = true
            }));
            await RejectBinding("nonobject-input", Binding(CerebralJson.Element("not an input object")));
            await RejectBinding("missing-setting-field", CerebralJson.Element(new
            {
                schemaVersion = 1, definitionId = ProgrammingFixtureDefinition.Id,
                definitionVersion = ProgrammingFixtureDefinition.Version
            }));
            var oversizedText = Binding(acceptedInput).GetRawText()
                .Replace("fixture.csproj", new string('é', 4000) + ".csproj", StringComparison.Ordinal);
            using (var oversized = JsonDocument.Parse(oversizedText))
            {
                Check(Encoding.UTF8.GetByteCount(oversizedText) > 8192 && oversizedText.Length < 8192,
                    "The oversized fixture crosses the UTF-8 byte bound while remaining below 8192 characters.");
                await RejectBinding("utf8-size", oversized.RootElement);
            }
            Check(models.Preparations == 0 && plugin.Definition.Advances == 0 &&
                plugin.DelegateDefinition.Advances == 0 && plugin.CollaborateDefinition.Advances == 0 && tools.Executions == 0,
                "Rejected waves execute no model, plugin workflow or semantic tool.");
            await CheckUnavailableCatalog("invalid-binding-catalog");

            SetBinding(Binding(acceptedInput));
            var catalog = await Invoke("binding-catalog", WorkerWorkflowEffects.CatalogCommand, WorkflowContract.EmptyObject);
            Check(catalog.Result.State == WorkflowEffectState.Succeeded, "Helper catalog is available through actual command admission.");
            var helper = catalog.Result.Data!.Value.GetProperty("helpers").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == "programmer");
            var advertisedWorkflow = helper.GetProperty("workflow");
            Check(advertisedWorkflow.GetProperty("definitionId").GetString() == ProgrammingFixtureDefinition.Id &&
                advertisedWorkflow.GetProperty("definitionVersion").GetString() == ProgrammingFixtureDefinition.Version &&
                advertisedWorkflow.GetProperty("description").GetString() == "Deterministic programming-shaped workflow" &&
                advertisedWorkflow.EnumerateObject().Count() == 3,
                "The helper advertises only its exact compact workflow identity and description.");
            Check(!helper.GetRawText().Contains(root, StringComparison.Ordinal) &&
                !helper.GetRawText().Contains("rootPath", StringComparison.Ordinal) &&
                !helper.GetRawText().Contains("projectFile", StringComparison.Ordinal) &&
                !helper.GetRawText().Contains("schemaVersion", StringComparison.Ordinal),
                "The helper catalog excludes configured input and private settings.");
            host.Store.CompleteEffect(catalog.Parent.Id, catalog.Context.EffectId, catalog.Result);
            host.Store.Cancel(catalog.Parent.Id);

            // This helper is valid only for the accepted parent project. A supplied effect context
            // with the same owner IDs may vary ProjectId or ParentRunId, but cannot choose helper metadata.
            var projectBound = CerebralJson.Read<AgentProfile>(
                CerebralJson.Element(state.Get<AgentProfile>("agent", "legacy")!));
            projectBound.Id = "project-bound";
            projectBound.Name = "project-bound";
            projectBound.PluginSettings[SettingsKey] = Binding(CerebralJson.Element(new { projectId = ProjectId }),
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion);
            state.Put("agent", projectBound.Id, projectBound);
            var pagedCatalog = await Invoke("binding-catalog-v2", WorkerWorkflowEffects.CatalogCommand,
                WorkflowContract.EmptyObject, commandVersion: 2);
            Check(pagedCatalog.Result.State == WorkflowEffectState.Succeeded,
                "Version-two helper catalog is admitted through the actual workflow command registry.");
            WorkflowSchema.Validate(pagedCatalog.Result.Data!.Value, pagedCatalog.Handler.Descriptor.OutputSchema);
            var acceptedPage = pagedCatalog.Result.Data!.Value
                .Deserialize<WorkerWorkflowEffects.CatalogPageData>(CerebralJson.Options)!;
            var changedProject = pagedCatalog.Context with
                { Submission = pagedCatalog.Context.Submission with { ProjectId = "different-project" } };
            var changedParent = pagedCatalog.Context with
                { Submission = pagedCatalog.Context.Submission with { ParentRunId = "foreign-parent" } };
            var projectResult = await pagedCatalog.Handler.ExecuteAsync(changedProject, default);
            var parentResult = await pagedCatalog.Handler.ExecuteAsync(changedParent, default);
            if (projectResult.Data is { } projectData) WorkflowSchema.Validate(projectData, pagedCatalog.Handler.Descriptor.OutputSchema);
            if (parentResult.Data is { } parentData) WorkflowSchema.Validate(parentData, pagedCatalog.Handler.Descriptor.OutputSchema);
            var projectPage = projectResult.Data?.Deserialize<WorkerWorkflowEffects.CatalogPageData>(CerebralJson.Options);
            var parentPage = parentResult.Data?.Deserialize<WorkerWorkflowEffects.CatalogPageData>(CerebralJson.Options);
            Check(projectResult.State == WorkflowEffectState.Succeeded && parentResult.State == WorkflowEffectState.Succeeded &&
                acceptedPage.Helpers is { } acceptedHelpers && projectPage?.Helpers is { } projectHelpers &&
                parentPage?.Helpers is { } parentHelpers &&
                acceptedHelpers.Any(item => item.Id == "project-bound") &&
                projectHelpers.Select(item => item.Id).SequenceEqual(acceptedHelpers.Select(item => item.Id)) &&
                parentHelpers.Select(item => item.Id).SequenceEqual(acceptedHelpers.Select(item => item.Id)) &&
                models.Preparations == 0 && tools.Executions == 0,
                "Version-two helper metadata follows durable accepted project and parent identity, not same-owner supplied context fields.");
            host.Store.CompleteEffect(pagedCatalog.Parent.Id, pagedCatalog.Context.EffectId, pagedCatalog.Result);
            host.Store.Cancel(pagedCatalog.Parent.Id);

            var accepted = await Invoke("mixed-harness-wave", WorkerWorkflowEffects.DelegateCommand, Assignments());
            Check(accepted.Result.State == WorkflowEffectState.Succeeded, "Legacy Conversation and configured plugin children are admitted together.");
            var children = host.Store.ChildRunIds(accepted.Parent.Id).Select(host.Store.Get).ToArray();
            Check(children.Length == 2, "Exactly the two selected independent helpers are durable.");
            var legacy = children.Single(child => child.Submission.ProfileId == "legacy");
            var programmed = children.Single(child => child.Submission.ProfileId == "programmer");
            Check(legacy.Submission.DefinitionId == ConversationWorkflowModule.DefinitionId &&
                legacy.Submission.DefinitionVersion == ConversationWorkflowModule.DefinitionVersion &&
                legacy.Submission.Input.GetProperty("projectId").GetString() == ProjectId,
                "An unconfigured helper retains the legacy Conversation binding and project input.");
            Check(programmed.Submission.DefinitionId == ProgrammingFixtureDefinition.Id,
                "The configured helper keeps its exact registered workflow ID; observed " + programmed.Submission.DefinitionId + ".");
            Check(programmed.Submission.DefinitionVersion == ProgrammingFixtureDefinition.Version,
                "The configured helper keeps its exact registered workflow version; observed " + programmed.Submission.DefinitionVersion + ".");
            Check(JsonElement.DeepEquals(programmed.Submission.Input, acceptedInput),
                "The configured helper keeps its complete admitted input. Expected " + acceptedInput.GetRawText() +
                "; observed " + programmed.Submission.Input.GetRawText() + ".");
            Check(programmed.Submission.ProjectId == ProjectId && !programmed.Submission.Input.TryGetProperty("projectId", out _),
                "The plugin child's project is inherited host metadata, independent of its strict input schema.");
            Check(children.All(child => child.Submission.ParentRunId == accepted.Parent.Id &&
                child.Submission.ProjectId == ProjectId &&
                child.Submission.Caller is { Kind: EventActorKind.Service, Id: "cerebral.coordination" }) &&
                children.Select(child => child.Submission.ConversationId).Append(accepted.Parent.Submission.ConversationId).Distinct().Count() == 3,
                "Both children preserve host lineage/project provenance and separate conversations.");
            Check(children.All(child =>
            {
                var snapshot = WorkerExecutionSnapshot.Read(child.Submission.SettingsSnapshot);
                return snapshot.Profile.Role == "helper" && snapshot.Profile.ContextTokens == 8192 &&
                    snapshot.Profile.ResponseTokens == 1024 && snapshot.AllowedRoots.SequenceEqual(new[] { root }) &&
                    !snapshot.Capabilities.Contains("agents.delegate") && !snapshot.Capabilities.Contains("agents.collaborate") &&
                    !snapshot.Profile.Capabilities.Contains("agents.delegate") && !snapshot.Profile.Capabilities.Contains("agents.collaborate") &&
                    snapshot.Profile.PluginSettings.Count == 0;
            }), "Child snapshots retain their own limits/root while stripping recursive grants and arbitrary settings.");
            var packages = WorkerExecutionSnapshot.Read(programmed.Submission.SettingsSnapshot).PackageBindings;
            Check(packages.Any(package => package.Id == ProgrammingFixtureModule.ModuleId &&
                package.Version == ProgrammingFixtureDefinition.Version && package.Generation == "binding-fixture-generation") &&
                packages.Any(package => package.Id == WorkerWorkflowEffects.ModuleId),
                "The alternate child's exact feature generation and host package are frozen at admission.");
            var savedPlan = state.Get<JsonElement>("coordination-plan", accepted.Context.EffectId).GetRawText();
            Check(savedPlan.Contains(ProgrammingFixtureDefinition.Id, StringComparison.Ordinal) &&
                savedPlan.Contains(ProgrammingFixtureDefinition.Version, StringComparison.Ordinal) &&
                savedPlan.Contains("fixture.csproj", StringComparison.Ordinal),
                "The committed delegation plan retains the resolved immutable workflow binding.");

            Check((await host.Runtime.RunAsync(legacy.Id)).Status == WorkflowStatus.Completed,
                "The legacy Conversation child completes with deterministic model behavior.");
            Check((await host.Runtime.RunAsync(programmed.Id)).Status == WorkflowStatus.Completed,
                "The registered deterministic plugin-shaped child completes through the actual engine runtime.");
            var pluginResult = host.Store.Get(programmed.Id).Result!.Value;
            Check(JsonElement.DeepEquals(pluginResult.GetProperty("bindingInput"), acceptedInput),
                "The plugin consumes the complete admitted input. Expected " + acceptedInput.GetRawText() +
                "; observed " + pluginResult.GetProperty("bindingInput").GetRawText() + ".");
            Check(plugin.Definition.Advances == 1 && models.Preparations == 1 && tools.Executions == 0,
                "The plugin executes once with no compiler or semantic effects; only the legacy child uses deterministic generation.");
            Check(!models.SawParentPrivateContext && !models.SawDelegationGrammar,
                "The Conversation child receives no parent transcript or recursive delegation grammar.");

            var beforeRetry = children.Select(child => CerebralJson.Element(host.Store.Get(child.Id)).GetRawText()).ToArray();
            SetBinding(Binding(CerebralJson.Element(new { rootPath = root, projectFile = "replacement.csproj" })));
            var decisionsBeforeRetry = engine.Status().CommittedDecisions;
            var repeated = await accepted.Handler.ExecuteAsync(accepted.Context, default);
            Check(repeated.State == WorkflowEffectState.Succeeded &&
                repeated.Data!.Value.GetRawText() == accepted.Result.Data!.Value.GetRawText() &&
                repeated.ProviderReceiptId == accepted.Context.EffectId,
                "A repeated effect retrieves the original child identities after helper binding changes.");
            Check(host.Store.ChildRunIds(accepted.Parent.Id).SequenceEqual(children.Select(child => child.Id)) &&
                children.Select(child => CerebralJson.Element(host.Store.Get(child.Id)).GetRawText()).SequenceEqual(beforeRetry) &&
                state.Get<JsonElement>("coordination-plan", accepted.Context.EffectId).GetRawText() == savedPlan &&
                engine.Status().CommittedDecisions == decisionsBeforeRetry,
                "Retry neither retargets saved children nor replaces the plan or reruns engine planning.");
            host.Store.CompleteEffect(accepted.Parent.Id, accepted.Context.EffectId, accepted.Result);
            var parentClaim = host.Store.TryClaim(accepted.Parent.Id)!;
            host.Store.ApplyStep(parentClaim.Id, parentClaim.Revision,
                new(WorkflowContract.EmptyObject, new WaitForWorkflowRuns(children.Select(child => child.Id).ToArray())));
            var collected = host.Store.ResumeCompletedDependencies().Single(run => run.Id == accepted.Parent.Id);
            var outcomes = collected.Signal.Data.GetProperty("children").EnumerateArray().ToArray();
            var outcomeDiagnostic = collected.Signal.Data.GetRawText();
            Check(collected.Signal.Kind == WorkflowSignalKind.DependencyResults,
                "The existing barrier returns a dependency-result signal; observed " + collected.Signal.Kind + ". " + outcomeDiagnostic);
            Check(outcomes.Length == 2 && outcomes.Select(item => item.GetProperty("runId").GetString())
                .OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(children.Select(child => child.Id).OrderBy(id => id, StringComparer.Ordinal)),
                "The barrier contains exactly one outcome for each admitted child. " + outcomeDiagnostic);
            foreach (var originalChild in children)
            {
                var terminalChild = host.Store.Get(originalChild.Id);
                var outcome = outcomes.Single(item => item.GetProperty("runId").GetString() == terminalChild.Id);
                Check(outcome.GetProperty("status").GetString() == "Completed" &&
                    outcome.GetProperty("profileId").GetString() == terminalChild.Submission.ProfileId &&
                    outcome.GetProperty("revision").GetInt64() == terminalChild.Revision &&
                    !outcome.GetProperty("truncated").GetBoolean() &&
                    outcome.GetProperty("outcomeSha256").GetString() is { Length: 64 } digest && digest.All(Uri.IsHexDigit),
                    "The barrier preserves terminal status, profile, revision and a bounded sealed-outcome handle for " +
                    terminalChild.Submission.ProfileId + ". " + outcomeDiagnostic);
            }
            var legacyResult = host.Store.Get(legacy.Id).Result!.Value;
            Check(legacyResult.GetProperty("summary").GetString() == "LEGACY_EVIDENCE",
                "The retained Conversation child still contains its exact expected evidence. " + legacyResult.GetRawText());
            var legacyOutcome = outcomes.Single(item => item.GetProperty("runId").GetString() == legacy.Id);
            Check(legacyOutcome.GetProperty("summary").GetString() == "LEGACY_EVIDENCE",
                "The Conversation barrier outcome projects its exact declared summary evidence. " + outcomeDiagnostic);
            var programmedOutcome = outcomes.Single(item => item.GetProperty("runId").GetString() == programmed.Id);
            Check(pluginResult.GetProperty("text").GetString() == "PROGRAM_EVIDENCE" &&
                programmedOutcome.GetProperty("summary").GetString() == "PROGRAM_EVIDENCE",
                "The plugin child retains its exact evidence and the barrier projects its declared text result. " + outcomeDiagnostic);
            host.Store.Cancel(accepted.Parent.Id);
            Check(engine.Status().Error is null, "Binding rejections remain ordinary work evidence and leave the engine healthy.");
            Console.WriteLine($"Coordination harness binding checks passed: {checks} (actual engine/admission/SQLite; deterministic workflow/model, no compiler or live inference).");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(root).StartsWith("cerebral-coordination-binding-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe binding fixture cleanup target.");
            Directory.Delete(root, true);
        }
    }

    private sealed record Invocation(WorkflowRun Parent, IWorkflowCommandHandler Handler,
        WorkflowCommandContext Context, WorkflowCommandResult Result);

    private sealed class ProgrammingFixtureModule : IWorkflowModule
    {
        public const string ModuleId = "fixture.programming";
        public ProgrammingFixtureDefinition Definition { get; } = new();
        public string Id => ModuleId;
        public ProgrammingFixtureDefinition DelegateDefinition { get; } = new(ProgrammingFixtureDefinition.DelegateId, "agents.delegate");
        public ProgrammingFixtureDefinition CollaborateDefinition { get; } = new(ProgrammingFixtureDefinition.CollaborateId, "agents.collaborate");
        public IReadOnlyList<IWorkflowDefinition> Workflows => [Definition, DelegateDefinition, CollaborateDefinition];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
    }

    private sealed class ProgrammingFixtureDefinition(string definitionId = ProgrammingFixtureDefinition.Id, string? coordinationCapability = null) : IWorkflowDefinition
    {
        public const string Id = "fixture.programming:develop", Version = "1.0.0";
        public const string DelegateId = "fixture.programming:recursive-delegate";
        public const string CollaborateId = "fixture.programming:recursive-collaborate";
        public int Advances;
        public WorkflowDefinitionDescriptor Descriptor { get; } = new(definitionId, Version,
            "Deterministic programming-shaped workflow",
            JsonSerializer.SerializeToElement(new
            {
                type = "object", properties = new
                {
                    rootPath = new { type = "string", minLength = 1, maxLength = 32768 },
                    projectFile = new { type = "string", minLength = 1, maxLength = 4096 }
                },
                required = new[] { "rootPath", "projectFile" }, additionalProperties = false
            }),
            coordinationCapability is null
                ? ["filesystem.read", "filesystem.write", "process.execute"]
                : ["filesystem.read", "filesystem.write", "process.execute", coordinationCapability]);
        public WorkflowStep Advance(WorkflowRun run)
        {
            Advances++;
            return new(WorkflowContract.EmptyObject, new CompleteWorkflow(CerebralJson.Element(new
            {
                text = "PROGRAM_EVIDENCE", bindingInput = run.Submission.Input.Clone(), evidenceTier = "deterministic-workflow"
            })));
        }
    }

    private sealed class EmptyTools : IToolDispatcher
    {
        public bool FullAccessActive => false;
        public int Executions;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken token)
        {
            Executions++;
            throw new InvalidOperationException("This fixture executes no semantic tools.");
        }
    }

    private sealed class DeterministicModels : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor { get; } = new("binding-model-fixture", "1.0.0", ["text"]);
        public int Preparations;
        public bool SawParentPrivateContext, SawDelegationGrammar;
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Preparations++;
            var text = string.Join('\n', request.Messages.SelectMany(message => message.Content)
                .OfType<TextContentPart>().Select(part => part.Text));
            SawParentPrivateContext |= text.Contains("PARENT_ONLY_PRIVATE", StringComparison.Ordinal);
            SawDelegationGrammar |= request.ResponseSchema?.GetRawText().Contains("\"delegate\"", StringComparison.Ordinal) == true;
            return Task.FromResult<IPreparedModelGeneration>(new Prepared(request));
        }
        private sealed class Prepared(ModelGenerationRequest request) : IPreparedModelGeneration
        {
            public PreparedModelMetadata Metadata { get; } = new("fixture", "binding-model-fixture", "1.0.0",
                request.ModelId, 42, request.ContextLimit, request.ResponseLimit, "deterministic fixture");
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new ModelGenerationResult(ModelGenerationFinish.Stop,
                    """{"action":"final","text":"LEGACY_EVIDENCE"}""", 24, 42));
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
