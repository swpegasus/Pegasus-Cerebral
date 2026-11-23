using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>
/// Real WorkerWorkflows.Submit and temporary SQLite journal with a deterministic
/// module and host-owned observer gate. No Worker process, model, SDK or network.
/// Add this source to the normal Worker.Tests project only after integrated source
/// review; the standalone delivery fixture's linked Contracts/Core copies are not
/// needed in that normal project.
/// </summary>
internal static class CommittedRootUserInputWorkerSubmitChecks
{
    internal static async Task<int> RunAsync(string exactOwnedFixtureParent)
    {
        if (!Path.IsPathFullyQualified(exactOwnedFixtureParent) || !Directory.Exists(exactOwnedFixtureParent))
            throw new ArgumentException("Provide one existing absolute private fixture parent.", nameof(exactOwnedFixtureParent));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(exactOwnedFixtureParent));
        if (string.Equals(parent, Path.GetPathRoot(parent),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("The private fixture parent must be below a volume root.", nameof(exactOwnedFixtureParent));
        var root = Path.Combine(parent, "committed-input-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var scenarios = new List<Scenario>();
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("Committed-input Worker submit: " + label);
            checks++;
        }
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        ManualResetEventSlim? admissionEntered = null;
        ManualResetEventSlim? admissionRelease = null;
        Scenario? retainedScenario = null;
        Task<WorkflowRun>? retainedSubmission = null;
        string? retainedDiagnostic = null;
        try
        {
            // Hold the package admission delegate after a real durable commit. The
            // observer must still have no discovery or callback until this gate returns.
            var entered = new ManualResetEventSlim(); admissionEntered = entered;
            var releaseGate = new ManualResetEventSlim(); admissionRelease = releaseGate;
            var packageHeld = 0;
            var callbackUnderGate = 0;
            var callbackUnderLegacyLock = 0;
            Scenario? ordered = null;
            var gate = new ProbeGate();
            var scenario = new Scenario(root, "ordered", gate, (_, rid, create) =>
            {
                var accepted = create();
                if (rid == "first")
                {
                    Volatile.Write(ref packageHeld, 1);
                    entered.Set();
                    if (!releaseGate.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Fixture package gate was not released.");
                    Volatile.Write(ref packageHeld, 0);
                }
                return accepted;
            });
            ordered = scenario; scenarios.Add(scenario);
            gate.OnCallback = () =>
            {
                if (Volatile.Read(ref packageHeld) != 0) Interlocked.Exchange(ref callbackUnderGate, 1);
                var stateGate = typeof(StateStore).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(ordered!.State)!;
                if (!Monitor.TryEnter(stateGate)) Interlocked.Exchange(ref callbackUnderLegacyLock, 1);
                else Monitor.Exit(stateGate);
            };
            scenario.Attach();
            var request = scenario.Request("first", "conversation-first");
            var sending = Task.Run(() => scenario.Host.Submit(request, null));
            Exception? heldGateFailure = null;
            try
            {
                Check(entered.Wait(TimeSpan.FromSeconds(5)), "package gate reached after durable commit");
                Check(scenario.Host.Store.TryGet(request.RequestId) is not null,
                    "actual SQLite run exists while outer package gate remains held");
                Check(gate.DiscoveredCount("first") == 0 && gate.CallbackCount == 0,
                    "no body-free discovery or callback under package admission gate");
            }
            catch (Exception error) { heldGateFailure = error; }
            finally { releaseGate.Set(); }
            // Bound the wait after releasing admission. A timeout cannot cancel
            // Submit; retain its host and SQLite subtree until an external review.
            WorkflowRun? submitted = null;
            Exception? submitFailure = null;
            try { submitted = await sending.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException error)
            {
                if (sending.IsCompleted)
                {
                    // The submission itself may have thrown TimeoutException, or
                    // completed at the wait deadline. Observe its actual outcome.
                    try { submitted = await sending.ConfigureAwait(false); }
                    catch (Exception completedError) { submitFailure = completedError; }
                }
                else
                {
                    retainedScenario = scenario;
                    retainedSubmission = sending;
                    retainedDiagnostic = "Worker fixture Submit remains unfinished after the five-second " +
                        "post-gate deadline. Retained active host and exact fixture subtree for diagnosis: " + root;
                    _ = sending.ContinueWith(completed => { _ = completed.Exception; },
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    submitFailure = new TimeoutException(retainedDiagnostic, error);
                }
            }
            catch (Exception error) { submitFailure = error; }
            if (heldGateFailure is not null)
            {
                if (submitFailure is not null) heldGateFailure.Data["submitFailure"] = submitFailure;
                if (retainedDiagnostic is not null) heldGateFailure.Data["retainedFixture"] = retainedDiagnostic;
                ExceptionDispatchInfo.Capture(heldGateFailure).Throw();
            }
            if (submitFailure is not null) ExceptionDispatchInfo.Capture(submitFailure).Throw();
            var saved = submitted ?? throw new InvalidOperationException("The submit task returned no run.");
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(saved.Id == request.RequestId && scenario.Host.Store.Get(saved.Id).Id == saved.Id &&
                !gate.Release.Task.IsCompleted, "accepted Send returns while observer callback remains blocked");
            Check(callbackUnderGate == 0 && callbackUnderLegacyLock == 0,
                "callback starts after package and legacy state gates release");

            var duplicate = scenario.Host.Submit(request, null);
            Check(duplicate.Id == saved.Id, "equivalent RID returns retained run");
            var rejected = false;
            try { _ = scenario.Host.Submit(request with { UserText = "changed" }, null); }
            catch (WorkflowConflictException) { rejected = true; }
            Check(rejected, "changed duplicate RID is rejected");
            rejected = false;
            try { _ = scenario.Host.Submit(scenario.Request("rejected", "conversation-rejected") with
                { Input = Element(new { unknown = true }) }, null); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected && scenario.Host.Store.TryGet("rejected") is null,
                "invalid schema rejects without a durable run");
            _ = scenario.Host.Submit(scenario.Request("sentinel", "conversation-sentinel"), null);
            await gate.SentinelDiscovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(gate.DiscoveredCount("first") == 1 && gate.DiscoveredCount("rejected") == 0,
                "later sentinel proves duplicate and rejected requests emitted no receipt");
            Check(scenario.Host.Journal.ReadPage("conversation-first").Events.Count(item =>
                item.Type == CerebralEventTypes.Message && item.Actor.Kind == EventActorKind.User) == 1,
                "actual journal has one accepted root User event for equivalent RID");

            await scenario.Host.DisposeAsync();
            Check(!scenario.Host.CommittedRootUserInputSettlement.IsCompleted &&
                scenario.Delivery!.TryEnqueue(Synthetic("after-stop")) == CommittedRootUserInputEnqueueResult.Stopped,
                "shutdown refuses new callbacks but cannot retire blocked lease yet");
            gate.Release.TrySetResult(true);
            await scenario.Host.CommittedRootUserInputSettlement.WaitAsync(TimeSpan.FromSeconds(5));
            Check(gate.DisposedCount == gate.CallbackCount,
                "retained settlement completes only after actual callback lease release");

            // Four uncooperative callbacks occupy delivery capacity. Synthetic coherent
            // receipts fill only this private queue; the final Send itself is real SQLite.
            var fullGate = new ProbeGate();
            var full = new Scenario(root, "full", fullGate); scenarios.Add(full); full.Attach();
            for (var i = 0; i < CommittedRootUserInputDelivery.MaximumActiveCallbacks; i++)
                Check(full.Delivery!.TryEnqueue(Synthetic("active-" + i)) == CommittedRootUserInputEnqueueResult.Queued,
                    "fixture active receipt admitted");
            await fullGate.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The pump may hold one receipt outside the channel while waiting for
            // a fifth callback slot. Put that receipt at a known fence and wait for
            // its body-free discovery before filling the channel behind it.
            Check(full.Delivery!.TryEnqueue(Synthetic("fence")) == CommittedRootUserInputEnqueueResult.Queued,
                "fixture dequeue fence admitted");
            await fullGate.FenceDiscovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < CommittedRootUserInputDelivery.QueueCapacity; i++)
                Check(full.Delivery!.TryEnqueue(Synthetic("pending-" + i)) == CommittedRootUserInputEnqueueResult.Queued,
                    "fixture pending receipt admitted");
            var beforeFull = full.Delivery!.FullCount;
            var acceptedWithFull = full.Host.Submit(full.Request("full-send", "full-conversation"), null);
            Check(acceptedWithFull.Id == "full-send" && full.Host.Store.TryGet("full-send") is not null &&
                full.Delivery.FullCount == beforeFull + 1,
                "full optional queue cannot turn committed Send into failure");
            fullGate.Release.TrySetResult(true);
            await full.Host.DisposeAsync();
            await full.Host.CommittedRootUserInputSettlement.WaitAsync(TimeSpan.FromSeconds(5));

            var failGate = new ProbeGate { FailDiscovery = true };
            var failing = new Scenario(root, "failing", failGate); scenarios.Add(failing); failing.Attach();
            var acceptedWithFault = failing.Host.Submit(failing.Request("fault-send", "fault-conversation"), null);
            await failGate.DiscoveryFault.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(acceptedWithFault.Id == "fault-send" && failing.Host.Store.TryGet("fault-send") is not null,
                "optional observer discovery failure cannot change committed Send");

            var stopped = new Scenario(root, "stopped", new ProbeGate()); scenarios.Add(stopped); stopped.Attach();
            await stopped.Delivery!.DisposeAsync();
            await stopped.Delivery.Settlement.WaitAsync(TimeSpan.FromSeconds(5));
            var acceptedWithStopped = stopped.Host.Submit(stopped.Request("stopped-send", "stopped-conversation"), null);
            Check(acceptedWithStopped.Id == "stopped-send" && stopped.Host.Store.TryGet("stopped-send") is not null,
                "stopped optional delivery cannot change committed Send");

            var old = new Scenario(root, "zero-observer", new ProbeGate()); scenarios.Add(old);
            var oldRun = old.Host.Submit(old.Request("old-send", "old-conversation"), null);
            Check(oldRun.Id == "old-send" && old.Host.CommittedRootUserInputSettlement.IsCompleted &&
                old.Host.Journal.ReadPage("old-conversation").Events.Any(item =>
                    item.Type == CerebralEventTypes.Message && item.Actor.Kind == EventActorKind.User),
                "zero-observer host keeps ordinary durable Submit without a delivery queue");
        }
        catch (Exception error) { primaryFailure = error; }
        finally
        {
            foreach (var scenario in scenarios.AsEnumerable().Reverse())
            {
                if (ReferenceEquals(scenario, retainedScenario)) continue;
                var settled = false;
                try { await scenario.DisposeAsync(); settled = true; }
                catch (Exception error) { cleanupFailures.Add(error); }
                if (retainedSubmission is not null && settled)
                {
                    try
                    {
                        var child = Path.GetFullPath(scenario.DirectoryPath);
                        if (!child.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                            throw new InvalidOperationException("Refusing cleanup outside the owned fixture subtree.");
                        Directory.Delete(child, recursive: true);
                    }
                    catch (Exception error) { cleanupFailures.Add(error); }
                }
            }
            if (retainedSubmission is null)
            {
                try { SqliteConnection.ClearAllPools(); }
                catch (Exception error) { cleanupFailures.Add(error); }
                try
                {
                    if (!Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar,
                            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        throw new InvalidOperationException("Refusing cleanup outside the exact fixture parent.");
                    Directory.Delete(root, recursive: true);
                }
                catch (Exception error) { cleanupFailures.Add(error); }
                try { admissionRelease?.Dispose(); }
                catch (Exception error) { cleanupFailures.Add(error); }
                try { admissionEntered?.Dispose(); }
                catch (Exception error) { cleanupFailures.Add(error); }
            }
        }
        if (retainedDiagnostic is not null)
        {
            try { Console.Error.WriteLine(retainedDiagnostic); }
            catch (Exception error) { cleanupFailures.Add(error); }
        }
        if (primaryFailure is not null)
        {
            if (cleanupFailures.Count != 0)
                primaryFailure.Data["cleanupFailures"] = new AggregateException(cleanupFailures);
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        if (cleanupFailures.Count != 0)
            throw new AggregateException("Committed-input Worker fixture cleanup failed.", cleanupFailures);
        return checks;
    }

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, CerebralJson.Options);

    private static CommittedRootUserInputReceipt Synthetic(string suffix) => new(
        "synthetic-" + suffix, "synthetic-conversation-" + suffix, "observer-profile",
        "committed.fixture:accept", "1.0.0", null, 0, "event-" + suffix,
        "synthetic-" + suffix, 1, CerebralEventTypes.Message,
        EventActorKind.User, EventSourceKind.User, null, "private fixture text");

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly string path;
        public string DirectoryPath => path;
        public StateStore State { get; }
        public WorkerWorkflows Host { get; }
        public CommittedRootUserInputDelivery? Delivery { get; private set; }
        public ProbeGate Gate { get; }
        public Scenario(string root, string name, ProbeGate gate,
            Func<string, string, Func<WorkflowRun>, WorkflowRun>? packageAdmission = null)
        {
            path = Path.Combine(root, name);
            Directory.CreateDirectory(path);
            State = new StateStore(Path.Combine(path, "data"));
            var configuration = new Configuration(State);
            var settings = configuration.Get(); settings.QueueCapacity = 256; configuration.Save(settings);
            var profile = new AgentProfile
            {
                Id = "observer-profile", Name = "Committed input fixture", Role = "lead",
                ModelId = "fixture-model", AutomaticCompaction = false,
                FollowsModelBudgets = false, ContextTokens = 8192, ResponseTokens = 512,
                MaxSteps = 64, AllowedRoots = [path], AllowedTools = [], Capabilities = []
            };
            State.Put("agent", profile.Id, profile);
            State.Put("model", profile.ModelId, new ModelRecord
            {
                Id = profile.ModelId, Path = Path.Combine(path, "fixture.gguf"), AutomaticContextSizing = false,
                ContextTokens = 8192, MaximumContextTokens = 8192, RecommendedContextTokens = 8192
            });
            var module = new Module();
            Host = new WorkerWorkflows(State, configuration, new EventHub(), [module],
                [new(module.Id, "1.0.0", true, [])], packageAdmission: packageAdmission);
            Gate = gate;
        }
        public SubmitWorkflowRequest Request(string id, string conversation) =>
            new(id, conversation, "observer-profile", "committed.fixture:accept", "1.0.0",
                "Exact accepted UserText", null, Element(new { }));
        public void Attach()
        {
            Delivery = new CommittedRootUserInputDelivery(Gate);
            Host.AttachCommittedRootUserInputDelivery(Delivery);
        }
        public async ValueTask DisposeAsync()
        {
            Gate.Release.TrySetResult(true);
            await Host.DisposeAsync();
            await Host.CommittedRootUserInputSettlement.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class Module : IWorkflowModule
    {
        public string Id => "committed.fixture";
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new Definition()];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
        private sealed class Definition : IWorkflowDefinition
        {
            public WorkflowDefinitionDescriptor Descriptor { get; } = new(
                "committed.fixture:accept", "1.0.0", "Committed input fixture",
                Element(new { type = "object", additionalProperties = false }), []);
            public WorkflowStep Advance(WorkflowRun snapshot) =>
                new(WorkflowContract.EmptyObject, new CompleteWorkflow(Element(new { accepted = true })));
        }
    }

    private sealed class ProbeGate : ICommittedRootUserInputDeliveryGate
    {
        private readonly CommittedRootUserInputObserverTarget target = new("fixture.observer", "generation-1", new object());
        private readonly ConcurrentDictionary<string, int> discovered = new(StringComparer.Ordinal);
        private int callbacks;
        private int disposed;
        public bool FailDiscovery;
        public Action? OnCallback;
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FourStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FenceDiscovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SentinelDiscovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> DiscoveryFault { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallbackCount => Volatile.Read(ref callbacks);
        public int DisposedCount => Volatile.Read(ref disposed);
        public int DiscoveredCount(string requestId) => discovered.TryGetValue(requestId, out var count) ? count : 0;
        public IReadOnlyList<CommittedRootUserInputObserverTarget> EligibleObservers(CommittedRootUserInputSource source)
        {
            if (FailDiscovery)
            {
                DiscoveryFault.TrySetResult(true);
                throw new InvalidOperationException("Fixture observer discovery failed.");
            }
            discovered.AddOrUpdate(source.RequestId, 1, (_, value) => value + 1);
            if (source.RequestId == "synthetic-fence") FenceDiscovered.TrySetResult(true);
            if (source.RequestId == "sentinel") SentinelDiscovered.TrySetResult(true);
            return [target];
        }
        public ICommittedRootUserInputCallbackLease? TryAcquire(
            CommittedRootUserInputObserverTarget candidate, CommittedRootUserInputSource source) =>
            ReferenceEquals(candidate.InstanceToken, target.InstanceToken) &&
            candidate.PackageId == target.PackageId && candidate.Generation == target.Generation
                ? new ProbeLease(this) : null;
        private sealed class ProbeLease(ProbeGate owner) : ICommittedRootUserInputCallbackLease
        {
            private int disposed;
            public async ValueTask InvokeAsync(CommittedRootUserInputObservation observation, CancellationToken token)
            {
                owner.OnCallback?.Invoke();
                var count = Interlocked.Increment(ref owner.callbacks);
                owner.Started.TrySetResult(true);
                if (count >= CommittedRootUserInputDelivery.MaximumActiveCallbacks)
                    owner.FourStarted.TrySetResult(true);
                // Ignore cancellation deliberately: this proves that a deadline or
                // shutdown request is not actual callback/package retirement.
                await owner.Release.Task.ConfigureAwait(false);
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                    Interlocked.Increment(ref owner.disposed);
            }
        }
    }
}
