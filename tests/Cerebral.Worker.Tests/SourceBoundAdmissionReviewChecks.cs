using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>
/// Actual WorkerWorkflows source-bound admission and Engine SQLite receipt.
/// A collectible fixture package supplies a pure definition and settings
/// policy; no scheduler, model, provider or Worker process runs.
/// </summary>
internal static class SourceBoundAdmissionReviewChecks
{
    internal static async Task<int> RunAsync(string exactOwnedFixtureParent)
    {
        if (!Path.IsPathFullyQualified(exactOwnedFixtureParent) ||
            !Directory.Exists(exactOwnedFixtureParent))
            throw new ArgumentException("Provide an existing absolute private fixture parent.");
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(exactOwnedFixtureParent));
        if (parent == Path.GetPathRoot(parent))
            throw new ArgumentException("The fixture parent must be below a volume root.");
        var root = Path.Combine(parent, "source-bound-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Source-bound admission: " + description);
            checks++;
        }
        try
        {
            var state = new StateStore(Path.Combine(root, "state"));
            var configuration = new Configuration(state);
            var sourceProfile = new AgentProfile
            {
                Id = "source-profile", Name = "Source fixture", Revision = 1,
                WorkerId = "local", ModelWorkerId = "local", ModelId = "fixture-model",
                AutomaticCompaction = false, FollowsModelBudgets = false,
                ContextTokens = 4096, ResponseTokens = 256, Capabilities = []
            };
            var workProfile = new AgentProfile
            {
                Id = "work-profile", Name = "Work fixture", Revision = 1,
                WorkerId = "local", ModelWorkerId = "local", ModelId = "fixture-model",
                AutomaticCompaction = false, FollowsModelBudgets = false,
                ContextTokens = 4096, ResponseTokens = 256, Capabilities = []
            };
            state.Put("agent", sourceProfile.Id, sourceProfile);
            state.Put("agent", workProfile.Id, workProfile);
            state.Put("model", "fixture-model", new ModelRecord
            {
                Id = "fixture-model", Path = Path.Combine(root, "fixture.gguf"),
                AutomaticContextSizing = false, ContextTokens = 4096,
                MaximumContextTokens = 4096, RecommendedContextTokens = 4096
            });
            var packageId = SourceBoundAdmissionFixturePlugin.PackageId;
            var package = Path.Combine(root, "packages", packageId);
            Directory.CreateDirectory(package);
            var assembly = typeof(SourceBoundAdmissionFixturePlugin).Assembly.Location;
            File.Copy(assembly, Path.Combine(package, Path.GetFileName(assembly)));
            File.WriteAllText(Path.Combine(package, ServicePluginRuntime.ManifestFileName),
                JsonSerializer.Serialize(new ServicePluginManifest
                {
                    Id = packageId, Name = "Source-bound admission fixture", Version = "1.0.0",
                    Assembly = Path.GetFileName(assembly),
                    EntryType = typeof(SourceBoundAdmissionFixturePlugin).FullName!
                }, CerebralJson.Options));
            using var runtime = new ServicePluginRuntime(Path.Combine(root, "packages"),
                Path.Combine(root, "package-data"), new EventHub(),
                (id, version, policy) => new ScopedPluginSettingsAccess(state, id, version, policy));
            Check(runtime.Status.Single().Loaded, "exact fixture service package is loaded");
            var preferences = new SettingsPreferencesStore(state, () => runtime.SettingsPolicies);
            var saved = preferences.SavePlugin(new(new(packageId,
                SettingsPreferenceContract.GlobalScope, null, "1.0.0", 1,
                CerebralJson.Element(new { enabled = true })), 0));
            Check(saved.Changed && saved.State.Revision == 1 && saved.State.Active is not null,
                "central active settings have an exact revision");
            var pendingComplete = preferences.PlanCompletePendingIncoming(saved.State.Active!);
            preferences.CommitTransferStates([pendingComplete]);
            var acceptedSettings = preferences.GetTransfer(saved.State.Coordinate).State;
            Check(acceptedSettings.Revision == saved.State.Revision + 1 &&
                acceptedSettings.Active is not null && acceptedSettings.Pending.Count == 1,
                "complete-pending state retains active settings at its exact newer revision");
            var legacyProjectionRefused = false;
            try { _ = preferences.GetPlugin(saved.State.Coordinate); }
            catch (InvalidOperationException) { legacyProjectionRefused = true; }
            Check(legacyProjectionRefused,
                "fixture exercises valid complete state that the legacy-only projection cannot represent");

            var sourceModule = new SourceModule();
            var bindings = runtime.PackageBindings.Concat([
                new ServicePluginPackageBinding(sourceModule.Id, "1.0.0", true, [])
            ]).ToArray();
            await using var workflows = new WorkerWorkflows(state, configuration,
                new EventHub(), [sourceModule, ..runtime.WorkflowModules], bindings,
                tools: new NoopTools(), contextRuntime: runtime);
            var sourceRun = await workflows.SubmitAsync(new("source-request", "source-conversation",
                sourceProfile.Id, SourceModule.DefinitionId, "1.0.0",
                "Exact first User request", null, WorkflowContract.EmptyObject), null, default);
            var receipt = workflows.Store.ReadFirstCommittedRootUserInput(sourceRun.Submission.ConversationId)
                ?? throw new InvalidOperationException("The actual first-root User receipt is missing.");
            var observation = new CommittedRootUserInputObservation(
                new(receipt.RequestId, receipt.ConversationId, receipt.ProfileId,
                    receipt.DefinitionId, receipt.DefinitionVersion, receipt.ProjectId,
                    receipt.EventId, receipt.EventSequence), receipt.UserText);
            var intent = new SourceBoundBackgroundIntent(
                SourceBoundAdmissionFixturePlugin.DefinitionId, "1.0.0",
                "work-request", "work-conversation", workProfile.Id, workProfile.Revision,
                acceptedSettings.Revision, "Generate a concise title", WorkflowContract.EmptyObject, 4);
            var binding = runtime.PackageBindings.Single(item => item.Id == packageId);
            var first = await workflows.SubmitSourceBoundBackgroundAsync(
                observation, intent, binding, default);
            Check(first.State == SourceBoundBackgroundAdmissionState.Admitted &&
                workflows.Store.TryGet(intent.WorkRequestId) is not null,
                "host admits one exact Service work request into durable SQLite");

            var revised = preferences.SaveTransfer(new(new(new(packageId,
                SettingsPreferenceContract.GlobalScope, null, "1.0.0", 1,
                CerebralJson.Element(new { enabled = false })), acceptedSettings.Revision)));
            workProfile.Revision++;
            state.Put("agent", workProfile.Id, workProfile);
            Check(revised.Changed && revised.State.State.Revision == acceptedSettings.Revision + 1,
                "settings and selected profile advance after accepted work");
            var duplicate = await workflows.SubmitSourceBoundBackgroundAsync(
                observation, intent, binding, default);
            Check(duplicate.State == SourceBoundBackgroundAdmissionState.Existing &&
                duplicate.WorkRequestId == first.WorkRequestId,
                "equivalent duplicate RID returns its original frozen run after later drift");
            var rejected = false;
            try
            {
                _ = await workflows.SubmitSourceBoundBackgroundAsync(
                    observation, intent with { UserText = "Changed request" }, binding, default);
            }
            catch (WorkflowConflictException) { rejected = true; }
            Check(rejected, "changed duplicate RID cannot recapture source or overwrite retained work");
            return checks;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            // The copied Worker.Tests assembly remains reachable until process exit;
            // runtime disposal only attempts collectible ALC unload.
            Console.WriteLine("Retained collectible fixture root for post-exit cleanup: " + root);
        }
    }

    private sealed class SourceModule : IWorkflowModule
    {
        internal const string DefinitionId = "fixture.source:accept";
        public string Id => "fixture.source";
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new SourceDefinition()];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
    }

    private sealed class SourceDefinition : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } =
            new(SourceModule.DefinitionId, "1.0.0", "Source fixture",
                WorkflowContract.EmptyObject, []);
        public WorkflowStep Advance(WorkflowRun snapshot) =>
            new(WorkflowContract.EmptyObject,
                new CompleteWorkflow(WorkflowContract.EmptyObject));
    }

    // Scheduler admission fixtures share this capture-only dispatcher.
    internal sealed class NoopTools : IToolDispatcher
    {
        public bool FullAccessActive { get; set; }
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Admission fixture cannot execute a tool.");
        public Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context,
            ToolExecutionEvidence? previousEvidence, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Admission fixture cannot reconcile a tool.");
    }
}

public sealed class SourceBoundAdmissionFixturePlugin : IServicePlugin, IWorkflowModule, IPluginSettingsPolicy
{
    public const string PackageId = "fixture.source-bound-admission";
    public const string DefinitionId = PackageId + ":work";
    public string Id => PackageId;
    public IReadOnlyList<IToolExtension> ToolExtensions => [];
    public IReadOnlyList<IServiceCommandExtension> CommandExtensions => [];
    public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new WorkDefinition()];
    public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
    public void Initialize(IServicePluginHost host) { }
    public void Dispose() { }
    public PluginSettingsAdmission Admit(PluginSettingsDocument incoming,
        JsonElement? currentValues, bool portable) =>
        new(true, 1, incoming.Values.Clone());
    public PluginSettingsProjection ExportPortable(PluginSettingsDocument current) =>
        new(1, current.Values.Clone());

    private sealed class WorkDefinition : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } =
            new(DefinitionId, "1.0.0", "Source-bound work fixture",
                WorkflowContract.EmptyObject, []);
        public WorkflowStep Advance(WorkflowRun snapshot) =>
            new(WorkflowContract.EmptyObject,
                new CompleteWorkflow(WorkflowContract.EmptyObject));
    }
}
