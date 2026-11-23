using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>Real host snapshot/journal/scheduler paths with temporary state and a deterministic Cortex transport.
/// No provider connection, model process, inference, owner data or installed service is used.</summary>
internal static class NativeContinuityAdmissionSchedulerTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-native-consent-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Native continuity admission/scheduler: " + message);
            checks++;
        }
        try
        {
            await ConsentAsync(root, Check);
            await SchedulerAsync(root, Check);
            Console.WriteLine($"Native continuity admission/scheduler: {checks} deterministic assertions passed.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temporary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Path.GetFileName(root).StartsWith("cerebral-native-consent-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing fixture cleanup outside its owned temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ConsentAsync(string root, Action<bool, string> check)
    {
        var missing = NativeCortexContinuityTests.Fixture.Create(root, "missing-consent");
        missing.Store.Delete("project-knowledge", missing.ProjectId);
        var missingSnapshot = await CaptureAsync(missing);
        check(missingSnapshot.NativeContinuity is null &&
            missing.Store.Get<ProjectKnowledgeSettings>("project-knowledge", missing.ProjectId) is { AutomaticContinuity: false },
            "retrieval defaults created by scope resolution do not manufacture continuity consent");

        var optedOut = NativeCortexContinuityTests.Fixture.Create(root, "explicit-opt-out", enabled: false);
        check((await CaptureAsync(optedOut)).NativeContinuity is null,
            "explicit saved opt-out captures no continuity binding");

        var positive = NativeCortexContinuityTests.Fixture.Create(root, "positive-consent");
        var savedRevision = positive.Store.Get<ProjectKnowledgeSettings>("project-knowledge", positive.ProjectId)!.Revision;
        var snapshot = await CaptureAsync(positive);
        check(snapshot.NativeContinuity?.ProjectSettingsRevision == savedRevision && savedRevision > 0 &&
            snapshot.CortexScope?.ProjectId == positive.ProjectId,
            "positive saved consent freezes its exact settings revision and project scope");

        var absentBinding = JsonNode.Parse(CerebralJson.Element(snapshot).GetRawText())!.AsObject();
        absentBinding.Remove("nativeContinuity");
        check(WorkerExecutionSnapshot.Read(JsonSerializer.SerializeToElement(absentBinding, CerebralJson.Options)).NativeContinuity is null,
            "older version-three JSON with a missing optional binding remains opted out");
        check(WorkerExecutionSnapshot.Read(CerebralJson.Element(snapshot with { ResolvedSettingsVersion = 2 })).NativeContinuity is null,
            "version-two recovery cannot inherit or retain a new continuity binding");

        var late = NativeCortexContinuityTests.Fixture.Create(root, "late-opt-in", enabled: false);
        var admittedSnapshot = await CaptureAsync(late);
        var accepted = late.Submit("before-opt-in", settingsSnapshot: CerebralJson.Element(admittedSnapshot));
        var acceptedSettings = accepted.Submission.SettingsSnapshot.GetRawText();
        var projects = new ProjectKnowledge(late.Store, late.Configuration, new EventHub());
        var consent = projects.Get(late.ProjectId); consent.AutomaticContinuity = true; projects.Save(consent);
        var reopened = late.Workflows.Get(accepted.Id);
        late.Manager.Observe(reopened);
        for (var i = 0; i < 4; i++) await late.Manager.ProcessNextAsync(default);
        check(reopened.Submission.SettingsSnapshot.GetRawText() == acceptedSettings &&
            WorkerExecutionSnapshot.Read(reopened.Submission.SettingsSnapshot).NativeContinuity is null &&
            late.Tools.Calls.Count == 0,
            "enabling continuity after acceptance cannot add provider authority to the accepted snapshot");

        foreach (var toggleOff in new[] { false, true })
        {
            var race = NativeCortexContinuityTests.Fixture.Create(root, toggleOff ? "admission-toggle" : "admission-revision");
            var rejected = false;
            try
            {
                await CaptureAsync(race, _ =>
                {
                    var manager = new ProjectKnowledge(race.Store, race.Configuration, new EventHub());
                    var changed = manager.Get(race.ProjectId);
                    if (toggleOff) changed.AutomaticContinuity = false;
                    manager.Save(changed);
                    return Task.CompletedTask;
                });
            }
            catch (InvalidOperationException error) when (error.Message.Contains("continuity consent changed", StringComparison.Ordinal))
            { rejected = true; }
            check(rejected && race.Tools.Calls.Count == 0,
                toggleOff ? "revoking consent during descriptor discovery rejects admission without provider calls" :
                    "changing only the consent revision during descriptor discovery rejects admission without provider calls");
        }

        var disabled = NativeCortexContinuityTests.Fixture.Create(root, "disabled-provider");
        var host = disabled.Configuration.Get(); host.McpServers.Single(x => x.Id == "cortex").Enabled = false;
        disabled.Configuration.Save(host);
        var disabledSnapshot = await CaptureAsync(disabled);
        check(disabledSnapshot.NativeContinuity is null && disabledSnapshot.CortexScope?.ProviderFingerprint == "unavailable" &&
            disabled.Tools.Calls.Count == 0,
            "a disabled provider cannot capture ready continuity authority or execute a provider call");

        var malformedRejected = false;
        try { WorkerExecutionSnapshot.Read(CerebralJson.Element(snapshot with { NativeContinuity = new(0) })); }
        catch (InvalidOperationException) { malformedRejected = true; }
        check(malformedRejected, "a present zero-revision consent binding fails closed");
    }

    private static async Task<WorkerExecutionSnapshot.Snapshot> CaptureAsync(NativeCortexContinuityTests.Fixture fixture,
        Func<CancellationToken, Task>? beforeList = null)
    {
        var tools = new AdmissionTools(fixture.Tools, beforeList);
        return WorkerExecutionSnapshot.Read(await WorkerExecutionSnapshot.CaptureAsync(fixture.Store, fixture.Profile,
            fixture.Configuration, tools, [], default, projectId: fixture.ProjectId));
    }

    private static async Task SchedulerAsync(string root, Action<bool, string> check)
    {
        var fixture = NativeCortexContinuityTests.Fixture.Create(root, "stalled-maintenance");
        var settings = fixture.Configuration.Get(); settings.MaxConcurrentJobs = 1; fixture.Configuration.Save(settings);
        fixture.Profile.Capabilities.Add("diagnostics"); fixture.Store.Put("agent", fixture.Profile.Id, fixture.Profile);
        var events = new EventHub();
        var module = new CompleteModule();
        var workflows = new WorkerWorkflows(fixture.Store, fixture.Configuration, events, [module],
            [new(module.Id, "1.0.0", true, [])], fixture.Tools, journal: fixture.Journal);
        var candidate = fixture.Submit("maintenance");
        candidate = fixture.Workflows.TryClaim(candidate.Id)!;
        candidate = fixture.Workflows.ApplyStep(candidate.Id, candidate.Revision,
            new(WorkflowContract.EmptyObject, new CompleteWorkflow(WorkflowContract.EmptyObject)));
        workflows.Publish(candidate);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var active = 0; var maximumActive = 0;
        fixture.Tools.OnCall = async (call, token) =>
        {
            if (!call.Call.Name.EndsWith("cortex_start", StringComparison.Ordinal)) return null;
            Interlocked.Increment(ref calls);
            var currentActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, currentActive);
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            try
            {
                // Deliberately retain the transport task after cancellation. Scheduler shutdown must await cleanup.
                await release.Task;
                token.ThrowIfCancellationRequested();
                return null;
            }
            finally { Interlocked.Decrement(ref active); exited.TrySetResult(); }
        };

        await using var models = new ModelRuntime(fixture.Store, fixture.Configuration.Get, events);
        using var scheduler = new JobScheduler(fixture.Store, fixture.Configuration, new UnusedExecutor(), models, events,
            NullLogger<JobScheduler>.Instance, workflows);
        Task? stopping = null;
        var stopped = false;
        try
        {
            await scheduler.StartAsync(default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(scheduler.QueueMetrics().OwnerRunning == 0 && scheduler.QueueMetrics().OwnerQueued == 0,
                "stalled maintenance consumes no foreground running or queued capacity");
            var ordinary = await workflows.SubmitAsync(new("ordinary-work", "ordinary-unbound-chat", fixture.Profile.Id,
                CompleteDefinition.Id, "1.0.0", "Independent fixture work", null, WorkflowContract.EmptyObject), null, default);
            await UntilAsync(() => workflows.Store.Get(ordinary.Id).Status == WorkflowStatus.Completed);
            check(!release.Task.IsCompleted && Volatile.Read(ref active) == 1 &&
                workflows.Store.Get(ordinary.Id).Status == WorkflowStatus.Completed,
                "a separate real workflow finishes while Cortex maintenance remains stalled");
            await Task.Delay(650);
            check(Volatile.Read(ref calls) == 1 && Volatile.Read(ref maximumActive) == 1 &&
                scheduler.QueueMetrics().OwnerRunning == 0,
                "successive scheduler ticks do not launch an overlapping provider operation or occupy a model slot");
            stopping = scheduler.StopAsync(default);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(!stopping.IsCompleted && Volatile.Read(ref active) == 1,
                "shutdown cancels the maintenance token and waits for the exact outstanding transport task");
            release.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5)); stopped = true;
            check(exited.Task.IsCompleted && Volatile.Read(ref active) == 0 && Volatile.Read(ref calls) == 1,
                "shutdown awaits maintenance cleanup without replaying a cancelled provider operation");
        }
        finally
        {
            release.TrySetResult();
            if (!stopped) await (stopping ?? scheduler.StopAsync(default)).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        int observed;
        do { observed = Volatile.Read(ref maximum); if (observed >= value) return; }
        while (Interlocked.CompareExchange(ref maximum, value, observed) != observed);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Independent fixture workflow did not finish.");
            await Task.Delay(20);
        }
    }

    private sealed class AdmissionTools(IToolDispatcher inner, Func<CancellationToken, Task>? beforeList) : IToolDispatcher
    {
        public bool FullAccessActive => inner.FullAccessActive;
        public async Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken cancellationToken)
        {
            if (beforeList is not null) await beforeList(cancellationToken);
            return await inner.ListAsync(profile, cancellationToken);
        }
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken) =>
            inner.ExecuteAsync(call, context, cancellationToken);
        public Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context, ToolExecutionEvidence? previousEvidence,
            CancellationToken cancellationToken) => inner.ReconcileAsync(call, context, previousEvidence, cancellationToken);
    }

    private sealed class CompleteModule : IWorkflowModule
    {
        public string Id => "fixture.continuity-scheduler";
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new CompleteDefinition()];
        public IReadOnlyList<IWorkflowCommandHandler> Commands { get; } = [];
    }
    private sealed class CompleteDefinition : IWorkflowDefinition
    {
        public const string Id = "fixture.continuity-scheduler:complete";
        public WorkflowDefinitionDescriptor Descriptor { get; } = new(Id, "1.0.0", "Independent work fixture",
            WorkflowContract.EmptyObject, ["diagnostics"]);
        public WorkflowStep Advance(WorkflowRun run) => new(WorkflowContract.EmptyObject, new CompleteWorkflow(WorkflowContract.EmptyObject));
    }
    private sealed class UnusedExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) =>
            throw new InvalidOperationException("Fixture must not start legacy work or model inference.");
    }
}
