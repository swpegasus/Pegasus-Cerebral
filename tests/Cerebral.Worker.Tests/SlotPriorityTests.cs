using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Shared allocator and trusted host admission fixtures; no model process, network or GPU.</summary>
internal static class SlotPriorityTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks++;
        }

        var pool = new LlamaSlotPool(2);
        using (var one = await Acquire(pool, InferencePriority.Normal))
        using (var two = await Acquire(pool, InferencePriority.Normal))
        {
            Check(one.Id != two.Id && pool.AvailableCount == 0,
                "Normal calls borrow every free slot; no role capacity is reserved.");
            var owner = Acquire(pool, InferencePriority.Interactive);
            Check(!owner.IsCompleted, "Interactive admission does not preempt either active call.");
            one.Dispose();
            using var granted = await owner.WaitAsync(Bound);
            Check(granted.Id == one.Id && pool.AvailableCount == 0,
                "Interactive call receives only the next released slot.");
            one.Dispose();
            Check(pool.AvailableCount == 0, "Duplicate disposal cannot lend the same slot twice.");
        }
        Check(pool.AvailableCount == 2, "All confirmed disposals restore the shared two-slot capacity.");

        pool = new(1);
        var held = await Acquire(pool, InferencePriority.Normal);
        var normalOne = Acquire(pool, InferencePriority.Normal);
        var interactiveOne = Acquire(pool, InferencePriority.Interactive);
        var interactiveTwo = Acquire(pool, InferencePriority.Interactive);
        var normalTwo = Acquire(pool, InferencePriority.Normal);
        held.Dispose();
        using (var first = await interactiveOne.WaitAsync(Bound))
        {
            Check(!normalOne.IsCompleted && !interactiveTwo.IsCompleted && !normalTwo.IsCompleted,
                "Queued owner call overtakes older normal work without granting another slot.");
        }
        using (var second = await interactiveTwo.WaitAsync(Bound))
            Check(!normalOne.IsCompleted && !normalTwo.IsCompleted, "Interactive calls retain FIFO order.");
        using (var third = await normalOne.WaitAsync(Bound))
            Check(!normalTwo.IsCompleted, "Normal calls retain FIFO order after interactive work settles.");
        using (await normalTwo.WaitAsync(Bound)) { }
        Check(pool.AvailableCount == 1, "Priority adds no permanent owner reservation.");

        held = await Acquire(pool, InferencePriority.Normal);
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelled = Acquire(pool, InferencePriority.Interactive, cancellation.Token);
            var next = Acquire(pool, InferencePriority.Normal);
            cancellation.Cancel();
            await ExpectCancelled(cancelled);
            Check(!next.IsCompleted && pool.AvailableCount == 0,
                "Cancelling a pending owner promptly removes only that waiter.");
            held.Dispose();
            using (await next.WaitAsync(Bound)) { }
        }
        Check(pool.AvailableCount == 1, "Cancelled admission leaks no capacity.");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await ExpectCancelled(Acquire(pool, InferencePriority.Interactive, cancelled.Token));
            Check(pool.AvailableCount == 1, "Already-cancelled calls cannot consume an idle slot.");
        }

        held = await Acquire(pool, InferencePriority.Normal);
        var interrupted = Acquire(pool, InferencePriority.Interactive);
        pool.Complete();
        await Expect<ChannelClosedException>(interrupted);
        held.Dispose();
        Check(pool.AvailableCount == 0, "Stopping an instance ends its waiters and cannot refill its pool.");

        pool = new(2);
        var uncertain = await Acquire(pool, InferencePriority.Normal);
        held = await Acquire(pool, InferencePriority.Normal);
        var remaining = Acquire(pool, InferencePriority.Interactive);
        uncertain.Quarantine("Fixture provider stop is unknown.");
        uncertain.Dispose();
        Check(pool.Quarantined.Count == 1 && pool.AvailableCount == 0 && !remaining.IsCompleted,
            "Priority cannot recycle quarantined provider capacity.");
        held.Dispose();
        using (var safe = await remaining.WaitAsync(Bound))
        {
            Check(safe.Id != uncertain.Id, "A distinct confirmed slot remains usable beside quarantine.");
            var blocked = Acquire(pool, InferencePriority.Normal);
            safe.Quarantine("Fixture second provider stop is unknown.");
            safe.Dispose();
            await Expect<IOException>(blocked);
        }
        Check(pool.Quarantined.Count == 2 && pool.AvailableCount == 0,
            "All-slot quarantine fails pending admission without claiming release.");

        var scopeAdapter = new FixtureAdapter();
        var request = Request();
        using (WorkerInferencePriority.Enter(InferencePriority.Interactive))
        {
            var normalAdapter = WorkerInferencePriority.Apply(scopeAdapter, InferencePriority.Normal);
            await using (var prepared = await normalAdapter.PrepareAsync(request, default))
                Check(scopeAdapter.Observed.Last() == InferencePriority.Normal,
                    "Nested helper or compactor preparation explicitly overrides an owner scope.");
            Check(WorkerInferencePriority.Current == InferencePriority.Interactive,
                "Nested normal preparation restores its parent scope.");

            scopeAdapter.Throw = true;
            await Expect<IOException>(normalAdapter.PrepareAsync(request, default));
            Check(WorkerInferencePriority.Current == InferencePriority.Interactive,
                "Failed preparation restores the parent scope.");
        }
        Check(WorkerInferencePriority.Current == InferencePriority.Normal,
            "Disposing the owner scope restores unclassified normal priority.");

        scopeAdapter.Throw = false;
        var ownerAdapter = WorkerInferencePriority.Apply(scopeAdapter, InferencePriority.Interactive);
        await using (var prepared = await ownerAdapter.PrepareAsync(request, default))
        {
            Check(scopeAdapter.Observed.Last() == InferencePriority.Interactive &&
                WorkerInferencePriority.Current == InferencePriority.Normal,
                "Interactive priority flows across preparation awaits and does not escape to its caller.");
            await prepared.GenerateAsync(null, default);
            Check(scopeAdapter.GenerationPriority == InferencePriority.Normal,
                "A returned generation lease leaves no owner priority ambient during inference.");
        }

        var multimodalInner = new MultimodalFixtureAdapter();
        var multimodalAdapter = WorkerInferencePriority.Apply(multimodalInner, InferencePriority.Interactive);
        Check(multimodalAdapter is IMultimodalModelRuntimeAdapter &&
            ReferenceEquals(multimodalAdapter.Descriptor, multimodalInner.Descriptor),
            "Priority preserves the adapter's verified multimodal capability and descriptor.");
        IReadOnlyList<RemotePreparedImageInput> images = [new(0, 0, "image/png", 1, 1, [1, 2, 3])];
        await using (var prepared = await ((IMultimodalModelRuntimeAdapter)multimodalAdapter)
            .PrepareMultimodalAsync(request, images, default))
            Check(ReferenceEquals(images, multimodalInner.Images) &&
                multimodalInner.Observed.Last() == InferencePriority.Interactive &&
                WorkerInferencePriority.Current == InferencePriority.Normal,
                "Multimodal preparation keeps exact borrowed image ownership and scopes priority to acquisition.");
        using (WorkerInferencePriority.Enter(InferencePriority.Interactive))
        {
            multimodalInner.Throw = true;
            var multimodalNormal = (IMultimodalModelRuntimeAdapter)WorkerInferencePriority.Apply(
                multimodalInner, InferencePriority.Normal);
            await Expect<IOException>(multimodalNormal.PrepareMultimodalAsync(request, images, default));
            Check(WorkerInferencePriority.Current == InferencePriority.Interactive,
                "Failed multimodal preparation restores the parent scope.");
        }

        var ownerSubmission = Submission(new(EventActorKind.User, "owner"));
        Check(WorkerInferencePriority.ForSubmission(ownerSubmission) == InferencePriority.Interactive,
            "Trusted root owner conversation is interactive independently of profile role.");
        Check(WorkerInferencePriority.ForSubmission(ownerSubmission with { ParentRunId = "parent" }) == InferencePriority.Normal &&
            WorkerInferencePriority.ForSubmission(Submission(new(EventActorKind.Agent, "lead"))) == InferencePriority.Normal &&
            WorkerInferencePriority.ForSubmission(Submission(new(EventActorKind.Service, "cerebral.coordination"))) == InferencePriority.Normal &&
            WorkerInferencePriority.ForSubmission(Submission(null)) == InferencePriority.Normal &&
            WorkerInferencePriority.ForSubmission(ownerSubmission with { DefinitionId = "fixture:background" }) == InferencePriority.Normal,
            "Child, service, agent, missing provenance and unrelated definitions cannot inherit owner priority.");

        await CheckTransportBoundaryAsync(Check);
        await ConcurrentCancellationAsync(Check);
        Console.WriteLine($"Slot priority: {checks} deterministic host/allocator checks passed (no model, network, GPU or live settings).");
        await LogicalContextAffinityChecks.RunAsync();
    }

    private static Task<LlamaSlotPool.Lease> Acquire(LlamaSlotPool pool, InferencePriority priority,
        CancellationToken token = default)
    {
        using var scope = WorkerInferencePriority.Enter(priority);
        return pool.AcquireAsync(() => { }, token);
    }

    private static async Task ExpectCancelled(Task task)
    {
        try { await task.WaitAsync(Bound); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Pending admission was not cancelled.");
    }

    private static async Task Expect<T>(Task task) where T : Exception
    {
        try { await task.WaitAsync(Bound); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected failure: " + typeof(T).Name);
    }

    private static async Task ConcurrentCancellationAsync(Action<bool, string> check)
    {
        for (var round = 0; round < 16; round++)
        {
            var pool = new LlamaSlotPool(2);
            var first = await Acquire(pool, InferencePriority.Normal);
            var second = await Acquire(pool, InferencePriority.Normal);
            var cancellations = Enumerable.Range(0, 16).Select(_ => new CancellationTokenSource()).ToArray();
            var occupied = new ConcurrentDictionary<int, byte>();
            var work = cancellations.Select((cancellation, index) =>
                ConsumeAsync(index % 3 == 0 ? InferencePriority.Interactive : InferencePriority.Normal, cancellation.Token)).ToArray();
            async Task ConsumeAsync(InferencePriority priority, CancellationToken token)
            {
                try
                {
                    using var lease = await Acquire(pool, priority, token).WaitAsync(Bound);
                    if (!occupied.TryAdd(lease.Id, 0)) throw new InvalidOperationException("A slot was granted twice.");
                    await Task.Yield();
                    if (!occupied.TryRemove(lease.Id, out _)) throw new InvalidOperationException("Slot ownership was lost.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
            try
            {
                await Task.WhenAll(
                    Task.Run(() => { for (var index = 0; index < cancellations.Length; index += 2) cancellations[index].Cancel(); }),
                    Task.Run(first.Dispose), Task.Run(second.Dispose));
                await Task.WhenAll(work).WaitAsync(Bound);
                check(occupied.IsEmpty && pool.AvailableCount == 2 && pool.Quarantined.Count == 0,
                    "Simultaneous cancellation and release retain exact capacity: round " + round);
            }
            finally
            {
                first.Dispose(); second.Dispose();
                foreach (var cancellation in cancellations) cancellation.Dispose();
            }
        }
    }

    private static async Task CheckTransportBoundaryAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-slot-priority-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = new StateStore(root);
            var profile = new AgentProfile { Id = "fixture-lead", Name = "Fixture lead", Role = "lead" };
            state.Put("agent", profile.Id, profile);
            var workflows = new WorkerWorkflows(state, new Configuration(state), new EventHub(),
                [new FixtureModule()], [new("pegasus.conversation", "1.0.0", true, [])]);
            var input = new SubmitWorkflowRequest("agent-call", "agent-context", profile.Id,
                ConversationWorkflowModule.DefinitionId, "1.0.0", "Fixture request", null,
                CerebralJson.Element(new { priority = "interactive", caller = new { kind = "User", id = "owner" } }));
            var run = await workflows.SubmitAsync(input, profile, default);
            check(run.Submission.Caller?.Kind == EventActorKind.Agent &&
                WorkerInferencePriority.ForSubmission(run.Submission) == InferencePriority.Normal,
                "Model input and a lead role cannot elevate an authenticated agent submission.");

            var forged = CerebralJson.Element(new
            {
                input.RequestId, input.ConversationId, input.ProfileId, input.DefinitionId, input.DefinitionVersion,
                input.UserText, input.AssignmentText, input.Input, caller = new { kind = "User", id = "owner" }
            });
            try { WorkerWorkflows.Read<SubmitWorkflowRequest>(forged); }
            catch (JsonException)
            {
                check(true, "Transport rejects caller provenance that is absent from its public request contract.");
                return;
            }
            throw new InvalidOperationException("Transport accepted forged caller provenance.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static ModelGenerationRequest Request() => new("fixture-model",
        [new("user", [new TextContentPart("Fixture request")])], 4096, 128);

    private static WorkflowSubmission Submission(EventActor? caller) => new("fixture-run", "fixture-context", "fixture-profile",
        ConversationWorkflowModule.DefinitionId, "1.0.0", "Fixture request", null,
        WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, Caller: caller);

    private class FixtureAdapter : IModelRuntimeAdapter
    {
        internal readonly List<InferencePriority> Observed = [];
        internal InferencePriority GenerationPriority;
        internal bool Throw;
        public ModelAdapterDescriptor Descriptor { get; } = new("fixture.priority", "1", ["text"]);
        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            await Task.Yield();
            Observed.Add(WorkerInferencePriority.Current);
            if (Throw) throw new IOException("Fixture preparation failure.");
            return new Prepared(this);
        }
        private sealed class Prepared(FixtureAdapter owner) : IPreparedModelGeneration
        {
            public PreparedModelMetadata Metadata { get; } = new("fixture-instance", "fixture.priority", "1",
                "fixture-model", 1, 4096, 128, "Fixture");
            public Task<ModelGenerationResult> GenerateAsync(Action<string>? onText, CancellationToken token)
            {
                owner.GenerationPriority = WorkerInferencePriority.Current;
                return Task.FromResult(new ModelGenerationResult(ModelGenerationFinish.Stop, "Fixture", 1, 1));
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class MultimodalFixtureAdapter : FixtureAdapter, IMultimodalModelRuntimeAdapter
    {
        internal IReadOnlyList<RemotePreparedImageInput>? Images;
        public Task<IPreparedModelGeneration> PrepareMultimodalAsync(ModelGenerationRequest request,
            IReadOnlyList<RemotePreparedImageInput> images, CancellationToken token)
        {
            Images = images;
            return PrepareAsync(request, token);
        }
    }

    private sealed class FixtureModule : IWorkflowModule
    {
        public string Id => "pegasus.conversation";
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new Definition()];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
        private sealed class Definition : IWorkflowDefinition
        {
            public WorkflowDefinitionDescriptor Descriptor { get; } = new(ConversationWorkflowModule.DefinitionId,
                "1.0.0", "Fixture", CerebralJson.Element(new { type = "object" }), []);
            public WorkflowStep Advance(WorkflowRun snapshot) => new(WorkflowContract.EmptyObject,
                new CompleteWorkflow(WorkflowContract.EmptyObject));
        }
    }
}
