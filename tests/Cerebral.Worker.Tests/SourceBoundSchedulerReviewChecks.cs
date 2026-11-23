using System.Reflection;
using System.Text.Json.Nodes;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>
/// Actual JobScheduler and temporary Engine SQLite, with the local resource
/// state set to a deterministic paused fixture. No model or provider call.
/// </summary>
internal static class SourceBoundSchedulerReviewChecks
{
    internal static async Task<int> RunAsync(string exactOwnedFixtureParent)
    {
        if (!Path.IsPathFullyQualified(exactOwnedFixtureParent) ||
            !Directory.Exists(exactOwnedFixtureParent))
            throw new ArgumentException("Provide an existing absolute private fixture parent.");
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(exactOwnedFixtureParent));
        if (parent == Path.GetPathRoot(parent))
            throw new ArgumentException("The fixture parent must be below a volume root.");
        var root = Path.Combine(parent, "source-bound-scheduler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var capacityGate = new BlockingGate();
        var complete = false;
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Source-bound scheduler: " + description);
            checks++;
        }
        try
        {
            var state = new StateStore(Path.Combine(root, "state"));
            var configuration = new Configuration(state);
            var settings = configuration.Get();
            settings.MaxConcurrentJobs = 1;
            configuration.Save(settings);
            state.Put("agent", "local-profile", new AgentProfile
            {
                Id = "local-profile", Name = "Local fixture", WorkerId = "local",
                ModelWorkerId = "local", ModelId = "fixture-local-model", Revision = 1,
                AutomaticCompaction = false, FollowsModelBudgets = false,
                ContextTokens = 4096, ResponseTokens = 256, Capabilities = ["diagnostics"]
            });
            state.Put("agent", "remote-profile", new AgentProfile
            {
                Id = "remote-profile", Name = "Remote fixture", WorkerId = "local",
                ModelWorkerId = "gb10", ModelId = "remote-model", Revision = 1,
                Capabilities = ["diagnostics"]
            });
            state.Put("model", "fixture-local-model", new ModelRecord
            {
                Id = "fixture-local-model", Path = Path.Combine(root, "fixture-local.gguf"),
                AutomaticContextSizing = false, ContextTokens = 4096,
                MaximumContextTokens = 4096, RecommendedContextTokens = 4096
            });
            var module = new CompletionModule(capacityGate);
            var package = new ServicePluginPackageBinding(module.Id, "1.0.0", true, [],
                null, "fixture-generation");
            await using var workflows = new WorkerWorkflows(state, configuration, new EventHub(),
                [module], [package], tools: new SourceBoundAdmissionReviewChecks.NoopTools());
            await using var models = new ModelRuntime(state, configuration.Get, new EventHub());
            using var scheduler = new JobScheduler(state, configuration, new NoopExecutor(),
                models, new EventHub(), NullLogger<JobScheduler>.Instance, workflows);
            Exception? bodyFailure = null;
            try
            {
                var pauseStateProperty = typeof(JobScheduler).GetProperty("resourcePauseState",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Actual local resource state is unavailable.");
                pauseStateProperty.SetValue(scheduler, LocalResourceState.Paused);
                var start = typeof(JobScheduler).GetMethod("StartWorkflows",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Actual workflow scheduler path is unavailable.");

                var foreground = await workflows.SubmitAsync(new("foreground-local", "conversation-local",
                    "local-profile", CompletionModule.DefinitionId, "1.0.0", "Local request",
                    null, WorkflowContract.EmptyObject), null, default);
                var marker = Marker(configuration.Get().InstallationId);
                var snapshot = RemoteSnapshot(marker, package);
                var malformed = JsonNode.Parse(snapshot.GetRawText())!.AsObject();
                malformed.Remove("profile");
                var malformedSubmission = Submission("background-malformed", "conversation-malformed",
                    marker with { WorkRequestId = "background-malformed",
                        WorkConversationId = "conversation-malformed" },
                    System.Text.Json.JsonDocument.Parse(malformed.ToJsonString()).RootElement.Clone());
                workflows.Store.Submit(malformedSubmission);
                var background = Submission("background-remote", "conversation-remote",
                    marker with { WorkRequestId = "background-remote",
                        WorkConversationId = "conversation-remote" }, snapshot);
                workflows.Store.Submit(background);

                start.Invoke(scheduler, [CancellationToken.None]);
                await UntilAsync(() => workflows.Store.Get("background-remote").Status ==
                    WorkflowStatus.Completed && scheduler.QueueMetrics().OwnerRunning == 0);
                Check(workflows.Store.Get(foreground.Id).Status == WorkflowStatus.Queued,
                    "paused local-GPU foreground remains queued");
                Check(workflows.Store.Get("background-malformed").Status == WorkflowStatus.Waiting,
                    "malformed marker is held instead of recurring or hiding later work");
                Check(workflows.Store.Get("background-remote").Status == WorkflowStatus.Completed,
                    "verified remote background work uses otherwise spare capacity");

                var localQueued = workflows.Store.Get(foreground.Id);
                workflows.Store.Hold(localQueued.Id, localQueued.Revision,
                    "Fixture settles the earlier paused local request before the capacity case.");
                pauseStateProperty.SetValue(scheduler, LocalResourceState.Active);
                await workflows.SubmitAsync(new("foreground-capacity", "conversation-capacity",
                    "local-profile", CompletionModule.DefinitionId, "1.0.0",
                    "Capacity request", null, WorkflowContract.EmptyObject), null, default);
                var capacityMarker = marker with { WorkRequestId = "background-capacity",
                    WorkConversationId = "conversation-background-capacity" };
                workflows.Store.Submit(Submission("background-capacity",
                    "conversation-background-capacity", capacityMarker,
                    RemoteSnapshot(capacityMarker, package)));
                start.Invoke(scheduler, [CancellationToken.None]);
                await capacityGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(workflows.Store.Get("background-capacity").Status == WorkflowStatus.Queued,
                    "full ordinary scheduler capacity does not admit background work");
                capacityGate.Release.Set();
                await UntilAsync(() => workflows.Store.Get("foreground-capacity").Status ==
                    WorkflowStatus.Completed && scheduler.QueueMetrics().OwnerRunning == 0);

                pauseStateProperty.SetValue(scheduler, LocalResourceState.Paused);
                for (var index = 0; index < 401; index++)
                {
                    var id = "foreground-page-" + index;
                    workflows.Store.Submit(new WorkflowSubmission(id, "conversation-page-" + index,
                        "local-profile", CompletionModule.DefinitionId, "1.0.0",
                        "Paged local request", null, WorkflowContract.EmptyObject,
                        foreground.Submission.SettingsSnapshot));
                }
                var incompleteMarker = marker with { WorkRequestId = "background-incomplete",
                    WorkConversationId = "conversation-background-incomplete" };
                workflows.Store.Submit(Submission("background-incomplete",
                    "conversation-background-incomplete", incompleteMarker,
                    RemoteSnapshot(incompleteMarker, package)));
                start.Invoke(scheduler, [CancellationToken.None]);
                Check(workflows.Store.Get("background-incomplete").Status == WorkflowStatus.Queued,
                    "incomplete bounded foreground scan withholds background capacity");
            }
            catch (Exception error)
            {
                bodyFailure = error;
                throw;
            }
            finally
            {
                // Release the owned workflow before scheduler disposal or SQL cleanup.
                capacityGate.Release.Set();
                try { await UntilAsync(() => scheduler.QueueMetrics().OwnerRunning == 0); }
                catch when (bodyFailure is not null) { /* Preserve the primary failure and fixture root. */ }
            }
            complete = true;
            return checks;
        }
        finally
        {
            capacityGate.Release.Set();
            SqliteConnection.ClearAllPools();
            if (complete)
            {
                var resolved = Path.GetFullPath(root);
                if (!resolved.StartsWith(parent + Path.DirectorySeparatorChar,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing cleanup outside the exact fixture parent.");
                Directory.Delete(resolved, recursive: true);
            }
        }
    }

    private static async Task UntilAsync(Func<bool> settled)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!settled())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Scheduler fixture did not settle.");
            await Task.Delay(20);
        }
    }

    private static SourceBoundWorkflowBinding Marker(string ownerBinding)
    {
        var source = new SourceBoundWorkflowSourceFacts(1, ownerBinding,
            CompletionModule.PackageId, "1.0.0", "fixture-generation",
            "pegasus.conversation:chat", "1.0.1", "source-conversation", "source-profile",
            "source-request", "source-event", 1, new string('a', 64), null,
            "Source title", 0, 1, "remote-profile", 1);
        return new(1, "background-remote", "conversation-remote", source);
    }

    private static System.Text.Json.JsonElement RemoteSnapshot(SourceBoundWorkflowBinding marker,
        ServicePluginPackageBinding package)
    {
        var profile = new AgentProfile
        {
            Id = "remote-profile", Name = "Remote fixture", WorkerId = "local",
            ModelWorkerId = "gb10", ModelId = "remote-model", Revision = 1,
            Capabilities = ["diagnostics"]
        };
        var model = new WorkerExecutionSnapshot.ModelBinding("remote-model", "fixture-model.gguf",
            null, [], 4096, 1, 0, ModelWorkerId: "gb10",
            ModelWorkerInstallationId: "fixture-gb10-installation");
        var frozen = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version,
            profile, "", "", model, [], ["diagnostics"], [], [], false,
            "fixture-routing", [package], []) { SourceBoundBackground = marker };
        return WorkerExecutionSnapshot.Bounded(frozen);
    }

    private static WorkflowSubmission Submission(string id, string conversationId,
        SourceBoundWorkflowBinding marker, System.Text.Json.JsonElement snapshot) =>
        new(id, conversationId, "remote-profile", CompletionModule.DefinitionId, "1.0.0",
            "Background request", null,
            CerebralJson.Element(new SourceBoundWorkflowInput(1, marker.Source,
                WorkflowContract.EmptyObject)), snapshot, Caller: new(EventActorKind.Service,
                CompletionModule.PackageId));

    private sealed class CompletionModule(BlockingGate gate) : IWorkflowModule
    {
        internal const string PackageId = "fixture.background";
        internal const string DefinitionId = PackageId + ":flow";
        public string Id => PackageId;
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new CompletionDefinition(gate)];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
    }

    private sealed class CompletionDefinition(BlockingGate gate) : IWorkflowDefinition
    {
        public WorkflowDefinitionDescriptor Descriptor { get; } =
            new(CompletionModule.DefinitionId, "1.0.0", "Completion fixture",
                WorkflowContract.EmptyObject, ["diagnostics"]);
        public WorkflowStep Advance(WorkflowRun run)
        {
            if (run.Id == "foreground-capacity")
            {
                gate.Entered.TrySetResult();
                if (!gate.Release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Fixture capacity gate was not released.");
            }
            return new(WorkflowContract.EmptyObject,
                new CompleteWorkflow(WorkflowContract.EmptyObject));
        }
    }

    private sealed class BlockingGate
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
    }

    private sealed class NoopExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) =>
            throw new InvalidOperationException("Fixture has no ordinary agent jobs.");
    }
}
