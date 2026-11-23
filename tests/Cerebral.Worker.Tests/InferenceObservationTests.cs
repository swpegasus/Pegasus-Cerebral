using System.Text.Json;
using System.Reflection;
using System.Diagnostics;
using Cerebral.Tools;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Cerebral.Contracts.Models;
using Cerebral.Core;

namespace Cerebral.Worker.Tests;

// Deterministic in-memory/allocator/fake-prepared checks. No provider, model or Worker is launched.
public static class InferenceObservationTests
{
    private static readonly List<object> lifecycleEvidence = [];
    public static IReadOnlyList<object> LifecycleEvidence => lifecycleEvidence;
    public static async Task<string[]> RunAsync()
    {
        lifecycleEvidence.Clear();
        var passed = new List<string>();
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); passed.Add(label); }
        var owner = new ModelExecutionAccounting(coverageComplete: true);
        var capture = owner.Observation;
        capture.ConfigureIssuer("fixture-installation");
        var scheduler = capture.StartScheduler();
        var firstToken = new object(); var secondToken = new object();
        capture.AcceptExecution(scheduler, "exec-one", firstToken, new("exec-one", "profile", 4), "local");
        capture.AcceptExecution(scheduler, "exec-two", secondToken, new("exec-two", "profile", 5), "local");
        var count = capture.Snapshot();
        Check(count.Counts.CompleteExecutions == 2 && count.Counts.CompleteWorkingProfileIdentities == 1,
            "two accepted executions retain one distinct profile across revisions");
        capture.RetireExecution(scheduler, "exec-one", new object());
        Check(capture.Snapshot().Counts.CompleteExecutions == 2, "wrong ownership token cannot retire accepted execution");
        capture.RetireExecution(scheduler, "exec-one", firstToken);
        Check(capture.Snapshot().Counts.CompleteExecutions == 1, "exact scheduler finally retires one execution");
        capture.AcceptExecution(scheduler, "invalid", new(), new("invalid", new string('x', 257), 0), "local");
        Check(capture.Snapshot().Counts.CompleteExecutions is null, "invalid required accepted identity suppresses complete counts");

        var raw = owner.Begin(ModelOwnershipKind.LocalPreparation, "model");
        var rawRow = capture.Snapshot().Rows.Single(x => x.AccountingEntryId == raw.Id);
        Check(rawRow.CorrelationId == owner.ProcessGenerationId + ":" + raw.Id && rawRow.Execution is null &&
            rawRow.ExecutionAttribution == "unavailable", "raw correlation is actual accounting origin and entry without fabricated execution");

        var asyncOwner = new ModelExecutionAccounting();
        var coordinate = new InferenceExecutionCoordinate("one", "profile", 1);
        using (WorkerInferenceInvocation.Enter(new("stable-correlation", "task-generation", coordinate)))
        {
            var a = asyncOwner.Begin(ModelOwnershipKind.LocalPreparation, "model");
            var c = asyncOwner.Begin(ModelOwnershipKind.LocalPreparation, "model");
            var retry = asyncOwner.Observation.Snapshot().Rows;
            Check(retry.Length == 2 && retry.All(x => x.CorrelationId == "stable-correlation") &&
                retry.Select(x => x.AttemptId).Distinct().Count() == 2, "retry retains stable correlation and new accounting attempt");
            try { using var inner = WorkerInferenceInvocation.Enter(new("inner", "compaction", coordinate)); throw new IOException(); }
            catch (IOException) { }
            Check(WorkerInferenceInvocation.Current?.CorrelationId == "stable-correlation", "nested invocation scope restores in finally");
            a.Complete(); c.Complete();
        }
        Check(WorkerInferenceInvocation.Current is null, "outer invocation scope leaves no ambient identity");
        var isolated = new ModelExecutionAccounting();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        async Task Lane(int lane)
        {
            using var scope = WorkerInferenceInvocation.Enter(new("correlation-" + lane, "task-generation",
                new("execution-" + lane, "profile", lane)));
            if (Interlocked.Increment(ref arrived) == 2) ready.SetResult();
            await ready.Task; await Task.Yield();
            _ = isolated.Begin(ModelOwnershipKind.LocalPreparation, "model");
        }
        await Task.WhenAll(Task.Run(() => Lane(1)), Task.Run(() => Lane(2)));
        var concurrent = isolated.Observation.Snapshot().Rows;
        Check(concurrent.Length == 2 && concurrent.All(x => x.CorrelationId == "correlation-" + x.Execution!.ProfileRevision),
            "concurrent asynchronous preparations do not cross invocation coordinates");
        Check(WorkerInferenceInvocation.Current is null, "concurrent child scopes do not leak into parent");

        var queuedOwner = new ModelExecutionAccounting();
        var pool = new LlamaSlotPool(2);
        var one = queuedOwner.Begin(ModelOwnershipKind.LocalPreparation, "model");
        var two = queuedOwner.Begin(ModelOwnershipKind.LocalPreparation, "model");
        var wait = queuedOwner.Begin(ModelOwnershipKind.LocalPreparation, "model");
        var slot1 = await pool.AcquireAsync(one.Complete, default, _ => one.Unknown("native-slot-quarantined"));
        var slot2 = await pool.AcquireAsync(two.Complete, default, _ => two.Unknown("native-slot-quarantined"));
        one.Bind(instanceId: "instance"); two.Bind(instanceId: "instance"); wait.Bind(instanceId: "instance");
        one.NativeSlot(slot1.LeaseId, slot1.Id); two.NativeSlot(slot2.LeaseId, slot2.Id);
        using var waitingStop = new CancellationTokenSource();
        using var waitingCancellation = waitingStop.Token.Register(wait.Cancellation);
        var waiter = pool.AcquireAsync(wait.Complete, waitingStop.Token);
        var held = queuedOwner.Observation.Snapshot().Rows;
        Check(held.Length == 3 && held.Count(x => x.LeaseId is not null) == 2 && held.Count(x => x.Phase == "queued") == 1 &&
            slot1.LeaseId != slot2.LeaseId && pool.AvailableCount == 0,
            "two exact lease objects and one waiter remain distinct from three physical slots");
        waitingStop.Cancel();
        try { await waiter; throw new InvalidOperationException("waiter unexpectedly admitted"); } catch (OperationCanceledException) { }
        wait.Complete();
        Check(queuedOwner.Observation.Snapshot().Rows.Single(x => x.AccountingEntryId == wait.Id) is
            { CancellationRequested: true, Release: "confirmed", Terminal: null }, "cancellation before dispatch retains no fabricated terminal usage");
        slot1.Dispose();
        var replacement = await pool.AcquireAsync(() => { }, default);
        Check(replacement.Id == slot1.Id && replacement.LeaseId != slot1.LeaseId, "reused native integer receives a new exact lease identity");
        replacement.Dispose(); slot2.Dispose();

        foreach (var early in new[] { false, true })
        {
            var account = new ModelExecutionAccounting();
            var ownership = account.Begin(ModelOwnershipKind.LocalPreparation, "model");
            ownership.Bind(instanceId: "fixture-instance");
            var slots = new LlamaSlotPool(1);
            var lease = await slots.AcquireAsync(ownership.Complete, default, _ => ownership.Unknown("native-slot-quarantined"));
            ownership.NativeSlot(lease.LeaseId, lease.Id);
            var prepared = new FakePrepared(lease, early, new(ModelGenerationFinish.Stop, "PRIVATE_MODEL_TEXT", 7, 100)
                { PromptCache = new(100, 96, 4, 5) });
            await using var tracked = new AccountingPreparedGeneration(prepared, ownership);
            var result = await tracked.GenerateAsync(null, default);
            var row = account.Observation.Snapshot().Rows.Single();
            Check(row.Terminal?.Finish == "stop" && (early ? row.Release == "confirmed" && row.Phase == "released" :
                row.Release != "confirmed" && row.Phase == "terminal"), early ? "release before terminal does not resurrect ownership" :
                "terminal before release does not falsely free the slot");
            Check(row.Budget?.InputTokens == 100 && row.Budget.Response == 512 && row.Budget.SafetyReserve == 128 &&
                row.Cache?.CachedTokens == 96 && row.Cache.ProcessedTokens == 4 &&
                row.Terminal is { GeneratedTokenScope: "provider-unspecified", ReasoningTokens: null },
                "full input, reserves, cache and unspecified generation scope stay separate");
            await tracked.DisposeAsync();
            Check(account.Observation.Snapshot().Rows.Single().Release == "confirmed" && slots.AvailableCount == 1,
                "confirmed prepared disposal returns its real lease");
            var json = JsonSerializer.Serialize(account.Observation.Snapshot());
            Check(!json.Contains("PRIVATE_MODEL_TEXT", StringComparison.Ordinal), "terminal capture excludes generated model content");
        }

        var readyOwner = new ModelExecutionAccounting();
        var readyOwnership = readyOwner.Begin(ModelOwnershipKind.LocalPreparation, "model");
        var readyPool = new LlamaSlotPool(1);
        var readyLease = await readyPool.AcquireAsync(readyOwnership.Complete, default);
        using var beforeStop = new CancellationTokenSource();
        await using (var tracked = new AccountingPreparedGeneration(new FakePrepared(readyLease, false,
            new(ModelGenerationFinish.Stop, "")), readyOwnership, cancellationToken: beforeStop.Token))
        {
            beforeStop.Cancel();
            Check(readyOwner.Observation.Snapshot().Rows.Single().CancellationRequested, "ready cancellation observed before generation dispatch");
        }
        Check(readyPool.AvailableCount == 1 && readyOwner.Observation.Snapshot().Rows.Single().Terminal is null,
            "unused ready disposal releases without invented generation result");

        var cancelOwner = new ModelExecutionAccounting();
        var cancelOwnership = cancelOwner.Begin(ModelOwnershipKind.LocalPreparation, "model");
        var cancelPool = new LlamaSlotPool(1);
        var cancelLease = await cancelPool.AcquireAsync(cancelOwnership.Complete, default);
        using var afterStop = new CancellationTokenSource();
        await using (var tracked = new AccountingPreparedGeneration(new FakePrepared(cancelLease, true,
            new(ModelGenerationFinish.Cancelled, "", CancellationConfirmed: true), () => afterStop.Cancel()), cancelOwnership))
        {
            await tracked.GenerateAsync(null, afterStop.Token);
            var row = cancelOwner.Observation.Snapshot().Rows.Single();
            Check(row.CancellationRequested && row.Terminal is { Finish: "cancelled", CancellationConfirmed: true } &&
                row.Release == "confirmed", "post-dispatch cancellation preserves explicit terminal and release evidence");
        }

        var quarantineOwner = new ModelExecutionAccounting();
        var q = quarantineOwner.Begin(ModelOwnershipKind.LocalPreparation, "model");
        q.Bind(instanceId: "stopped-instance"); q.Phase(ModelOwnershipPhase.Generating, true); q.Unknown("native-slot-quarantined");
        q.Cancellation();
        var later = quarantineOwner.Observation.Snapshot(DateTimeOffset.UtcNow.AddDays(1));
        Check(later.Rows.Single().Phase == "quarantined" && later.Rows.Single().Release != "confirmed",
            "quarantine and unknown ownership survive history TTL and later cancellation");
        quarantineOwner.CompleteStoppedInstance("stopped-instance");
        Check(quarantineOwner.Observation.Snapshot().Rows.Single().ReleaseProof == "exact-instance-stopped",
            "exact stopped-instance cleanup resolves retained quarantine");
        Check(quarantineOwner.Observation.Snapshot(DateTimeOffset.UtcNow.AddMinutes(6)).Rows.Length == 0,
            "only confirmed released history expires");

        var bounded = new InferenceObservationRegistry("account", true, 2, 1);
        for (var i = 0; i < 4; i++) bounded.Accounting(Entry(i.ToString()), i + 1);
        var boundary = bounded.Snapshot(maximumRows: 1);
        Check(boundary.RetainedCurrentRows == 2 && boundary.Truncated && boundary.Partial &&
            boundary.Counts.CompleteExecutions is null && boundary.Reasons.Contains("capture-incomplete"),
            "observer bounds retain unresolved rows and mark missing coverage without throwing");
        bounded.Released("0", 10, "fixture-confirmed"); bounded.Released("1", 11, "fixture-confirmed");
        Check(bounded.Snapshot().Rows.Length == 1, "released history count stays independently bounded");
        Check(JsonSerializer.SerializeToUtf8Bytes(boundary).Length <= InferenceObservationRegistry.MaximumPayloadBytes,
            "returned registry payload remains bounded");
        bounded.Allocator("model", "instance", "runtime", 4, 2, 0, 0);
        await Task.Delay(5);
        bounded.Launched("model", "instance", "runtime", 5, 2, 16384, null);
        var aged = bounded.Snapshot(DateTimeOffset.UtcNow.AddSeconds(5)).Models.Single();
        Check(aged.AllocatorSource.AgeMilliseconds >= 5000 && aged.LaunchSource.AgeMilliseconds >= 5000 &&
            aged.AllocatorSource.GenerationId != aged.LaunchSource.GenerationId && aged.LaunchSource.Revision == 5 &&
            aged.ProviderObservedCapacity is null && aged.ProviderObservedBusySlots is null,
            "allocator and launch observations retain independent age/revision and missing provider counts");
        Check(boundary.OwnerConnectionRegistrationFence is null &&
            boundary.OwnerConnectionRegistrationFenceReason == "owner-connection-registration-fence-not-captured" &&
            boundary.RemoteState == "unsupported" && boundary.Counts.CompleteContextCount is null,
            "uncaptured owner fence remote aggregation and context totals stay explicit");

        var payload = new InferenceObservationRegistry("payload", true);
        using (WorkerInferenceInvocation.Enter(new("payload-correlation", "task-generation",
            new(new string('e', 256), new string('p', 256), 1, new string('c', 256),
                new string('q', 256), new string('r', 256), new string('f', 256)))))
        {
            for (var i = 0; i < 128; i++)
                payload.Accounting(Entry(i.ToString()), i + 1, WorkerInferenceInvocation.Current);
        }
        var payloadPage = payload.Snapshot();
        Check(payloadPage.Rows.Length < 128 && payloadPage.Truncated &&
            JsonSerializer.SerializeToUtf8Bytes(payloadPage, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length <= InferenceObservationRegistry.MaximumPayloadBytes,
            "maximum identifier payload enforces byte bound and incomplete coverage without dropping retained ownership");
        Check(payloadPage.RetainedCurrentRows == 128, "payload truncation never deletes retained unresolved rows");
        var inbound = new InferenceObservationRegistry("inbound", false);
        inbound.Accounting(Entry("reservation") with { Kind = ModelOwnershipKind.InboundReservation, LeaseId = "remote-prepared-lease" }, 1);
        var inboundRow = inbound.Snapshot().Rows.Single();
        Check(inboundRow.AccountingLeaseId == "remote-prepared-lease" && inboundRow.LeaseId is null &&
            inboundRow.NativeSlotId is null && inboundRow.Execution is null, "inbound lease provenance does not invent a nested local slot or execution join");

        var initial = capture.Snapshot();
        Check(!InferenceObservationRegistry.IsCurrent(initial, new("cerebral.inference-observation.changed/1", initial.IssuingInstallationId, "other-generation", initial.Revision + 1)) &&
            !InferenceObservationRegistry.IsCurrent(initial, new("cerebral.inference-observation.changed/1", initial.IssuingInstallationId, initial.ObserverGenerationId, initial.Revision - 1)),
            "stale revision and different observer generation invalidations are rejected");
        Check(!InferenceObservationRegistry.IsCurrent(initial, new("wrong-schema", initial.IssuingInstallationId,
            initial.ObserverGenerationId, initial.Revision)), "unknown invalidation schema cannot be adopted");
        Check(InferenceObservationRegistry.IsCurrent(initial, new("cerebral.inference-observation.changed/1", initial.IssuingInstallationId,
            initial.ObserverGenerationId, initial.Revision)), "matching generation invalidation can trigger a current reread");

        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureCapture = new InferenceObservationRegistry("failure", false);
        failureCapture.ConfigureIssuer("owner", _ => { notified.TrySetResult(); throw new IOException("fixture observer"); });
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 100 && failureCapture.Snapshot().EventDeliveryState != "unavailable"; i++) await Task.Delay(5);
        Check(failureCapture.Snapshot().EventDeliveryState == "unavailable", "observer exceptions remain diagnostic instead of escaping capture");
        var callbackCount = 0;
        var pure = new InferenceObservationRegistry("read-purity", true);
        var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pure.ConfigureIssuer("owner", _ => { Interlocked.Increment(ref callbackCount); notification.TrySetResult(); });
        await notification.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(10);
        var callbacksBefore = callbackCount;
        for (var i = 0; i < 100; i++)
            _ = CommandDispatcher.ReadCapturedInferenceObservation(pure, JsonSerializer.SerializeToElement(new { }), default);
        await Task.Delay(10);
        for (var i = 0; i < 100; i++)
            _ = CommandDispatcher.ReadCapturedInferenceObservationResponse(pure, CerebralJson.Element(new { schemaVersion = 2 }), default);
        Check(callbackCount == callbacksBefore, "snapshot and both command versions perform no observer callbacks or outside I/O");
        var ordinaryPool = new LlamaSlotPool(1); var returned = 0;
        var throwingObserver = await ordinaryPool.AcquireAsync(() => returned++, default);
        ordinaryPool.ObserveChanges(() => throw new IOException("fixture"));
        throwingObserver.Dispose();
        Check(ordinaryPool.AvailableCount == 1 && returned == 1, "throwing allocator observer cannot alter actual return or admission");
        var sharedPool = new LlamaSlotPool(1);
        var availability = new List<int>();
        sharedPool.ObserveChanges(() => availability.Add(sharedPool.AvailableCount));
        var rawLease = await sharedPool.AcquireAsync(() => { }, default);
        rawLease.Dispose();
        Check(availability.SequenceEqual(new[] { 0, 1 }), "shared allocator reports raw capability and image acquisition and return edges");
        var quarantinePool = new LlamaSlotPool(1);
        quarantinePool.ObserveChanges(() => throw new IOException("fixture pool observer"));
        var quarantineLease = await quarantinePool.AcquireAsync(() => throw new InvalidOperationException("must not return quarantine"), default);
        quarantineLease.Quarantine("fixture uncertainty"); quarantineLease.Dispose();
        Check(quarantinePool.AvailableCount == 0 && quarantinePool.Quarantined.Count == 1,
            "throwing shared allocator observer cannot return a quarantined lease");
        var unknownOwner = new ModelExecutionAccounting();
        var unknown = unknownOwner.Begin(ModelOwnershipKind.LocalPreparation, "model");
        unknown.Bind(instanceId: "unknown-instance"); unknown.Phase(ModelOwnershipPhase.Generating, true); unknown.Dispose();
        Check(unknownOwner.Observation.Snapshot(DateTimeOffset.UtcNow.AddDays(1)).Rows.Single() is
            { Phase: "unknown", Release: "unobserved" }, "scope exit after an effect never expires or invents provider release");
        var runtimeCapture = new InferenceObservationRegistry("runtime-account", true);
        Check(runtimeCapture.Snapshot().ModelCoverage.State == "unavailable", "uncaptured runtime model scope cannot become a complete empty inventory");
        runtimeCapture.RuntimeStarted("runtime-generation", 0);
        runtimeCapture.Allocator("model", "instance", "runtime-generation", 8, 2, 1, 0);
        runtimeCapture.Allocator("model", "instance", "runtime-generation", 7, 2, 2, 0);
        Check(runtimeCapture.Snapshot().Models.Single().ManagedAvailableSlots == 1,
            "older runtime epoch cannot overwrite newer allocator capture");
        runtimeCapture.Stopped("instance", "runtime-generation", 9);
        Check(runtimeCapture.Snapshot().Models.Length == 0 && runtimeCapture.Snapshot().ModelCoverage.Revision == 9,
            "direct stopped edge retires model inventory under its own generation");
        var progressOwner = new ModelExecutionAccounting();
        var progress = progressOwner.Begin(ModelOwnershipKind.LocalPreparation, "model");
        progress.Prepared(new("i", "a", "1", "model", 100, 16384, 512, LlamaProtocol.CountBasis));
        progress.Progress(new("prefill", 4, 5, null, null));
        var progressLater = progressOwner.Observation.Snapshot(DateTimeOffset.UtcNow.AddSeconds(3)).Rows.Single();
        Check(progressLater.ProgressSource.State == "stale" && progressLater.Budget?.InputTokens == 100 &&
            progressLater.ProcessedInputTokens == 4, "aged progress is stale and processed input never replaces full prepared input");

        var cancellationRejected = false;
        using var alreadyCancelled = new CancellationTokenSource(); alreadyCancelled.Cancel();
        try { CommandDispatcher.ReadCapturedInferenceObservation(pure, JsonSerializer.SerializeToElement(new { }), alreadyCancelled.Token); }
        catch (OperationCanceledException) { cancellationRejected = true; }
        Check(cancellationRejected, "cancelled read is bounded and performs no capture mutation");
        var lateOwner = new ModelExecutionAccounting(coverageComplete: true);
        lateOwner.Observation.ConfigureIssuer("owner"); lateOwner.Observation.StartScheduler();
        var retired = lateOwner.Begin(ModelOwnershipKind.LocalPreparation, "model");
        retired.Complete();
        _ = lateOwner.Observation.Snapshot(DateTimeOffset.UtcNow.AddMinutes(6));
        retired.Cancellation(); retired.Terminal(new(ModelGenerationFinish.Stop, ""));
        Check(lateOwner.Observation.Snapshot().Rows.Length == 0 && lateOwner.Observation.Snapshot().Counts.CompleteExecutions == 0,
            "late callbacks after confirmed history expiry neither resurrect rows nor fabricate missing active coverage");
        var rateOwner = new ModelExecutionAccounting();
        var rate = rateOwner.Begin(ModelOwnershipKind.LocalPreparation, "model");
        rate.Progress(new("prefill", 4, 5, null, null));
        rate.Progress(new("prefill", 5, 6, null, null));
        Check(rateOwner.Observation.Snapshot().Rows.Single().ProcessedInputTokens == 4,
            "same-phase numeric provider progress coalesces within 250ms");
        rate.Progress(new("output", 5, 6, 1, 1));
        Check(rateOwner.Observation.Snapshot().Rows.Single().Progress == "output",
            "provider phase transitions bypass numeric progress coalescing");
        await RemoteLinksAsync(Check);
        await SchedulerAndDispatcherAsync(Check);
        await OwnedLifecycleOrderingAsync(Check);
        return passed.ToArray();
    }

    private static async Task RemoteLinksAsync(Action<bool, string> check)
    {
        // Exercise the real receiver scope with a deterministic adapter sharing its fixture ledger.
        // The allocator and prepared generation are fakes; no native provider or Worker is launched.
        var adapter = new InboundLinkAdapter();
        await using var receiver = new RemotePreparedInference(adapter, (_, _) => Task.CompletedTask,
            prepareMultimodal: adapter.PrepareMultimodalAsync);
        var account = (ModelExecutionAccounting)typeof(RemotePreparedInference)
            .GetField("accounting", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(receiver)!;
        adapter.Accounting = account;
        var binding = new RemotePreparedModelBinding("model", "/fixture/model.gguf", null, [], 16384, 2, 0);
        var request = new ModelGenerationRequest("model", [new("user", [new TextContentPart("fixture input")])], 16384, 512);
        JsonElement Input(ModelGenerationRequest? value = null) => CerebralJson.Element(new RemotePrepareRequest(RemotePreparedProtocol.Version, value ?? request, binding));
        JsonElement Lease(string id, int version = RemotePreparedProtocol.Version) => CerebralJson.Element(new RemoteLeaseRequest(version, id));
        RemotePreparedResponse Prepared(CommandResult value) => CerebralJson.Read<RemotePreparedResponse>(value.Data);

        var inputKeys = new[] { "first fixture input", "second fixture input" };
        var preparations = await Task.WhenAll(inputKeys.Select(key => receiver.PrepareAsync(
            Input(request with { Messages = [new("user", [new TextContentPart(key)])] }), default)));
        adapter.WaitForPair = false;
        var remoteLeases = preparations.Select(value => Prepared(value).LeaseId!).ToArray();
        var page = account.Observation.SnapshotV2();
        var nativeRows = page.Snapshot.Rows.Where(row => row.Kind == nameof(ModelOwnershipKind.LocalPreparation)).ToArray();
        check(nativeRows.Length == 2 && nativeRows.All(row => row.InstanceId == "fixture-instance") &&
            nativeRows.Select(row => row.NativeSlotId).Distinct().Count() == 2 &&
            nativeRows.Select(row => row.LeaseId).Distinct().Count() == 2,
            "concurrent receiver preparations retain distinct fake native leases and slots on the same instance");
        check(page.Links.Length == 2 && page.Links.All(link => link.State == "observed" &&
            link.InboundPreparation?.AccountingGenerationId == account.ProcessGenerationId) &&
            page.Links.Select(link => link.InboundPreparation!.PreparedLeaseId).OrderBy(value => value)
                .SequenceEqual(remoteLeases.OrderBy(value => value)) &&
            Enumerable.Range(0, inputKeys.Length).All(index => page.Links.Single(link =>
                link.InboundPreparation?.PreparedLeaseId == remoteLeases[index]).RowId ==
                account.ProcessGenerationId + ":" + adapter.NativeEntries[inputKeys[index]]),
            "each receiver lease matches the native entry for its exact fixture input without timing inference");
        check(page.Links.All(link => page.Snapshot.Rows.Any(row =>
            row.AccountingEntryId == link.InboundPreparation!.AccountingEntryId &&
            row.Kind == nameof(ModelOwnershipKind.InboundReservation) &&
            row.AccountingLeaseId == link.InboundPreparation.PreparedLeaseId)) &&
            nativeRows.All(row => row.Execution is null && row.ExecutionAttribution == "unavailable") &&
            WorkerInferenceInvocation.Current is null,
            "receiver links identify actual inbound rows without fabricating execution and leave no ambient scope");

        await Task.WhenAll(remoteLeases.Select(id => receiver.GenerateAsync(Lease(id), default)));
        var completed = account.Observation.SnapshotV2();
        check(completed.Links.All(link => completed.Snapshot.Rows.Single(row => row.RowId == link.RowId) is
            { Release: "confirmed", Terminal.Finish: "stop", Cache.CachedTokens: 40 }) &&
            account.Snapshot().Entries.Length == 0 && adapter.Pool.AvailableCount == 2,
            "linked fake native release before terminal retains terminal and cache on the exact released row");
        var again = Prepared(await receiver.PrepareAsync(Input(), default));
        var reuse = account.Observation.SnapshotV2();
        var reusedLink = reuse.Links.Single(link => link.InboundPreparation?.PreparedLeaseId == again.LeaseId);
        var reusedRow = reuse.Snapshot.Rows.Single(row => row.RowId == reusedLink.RowId);
        check(nativeRows.Any(row => row.NativeSlotId == reusedRow.NativeSlotId) &&
            nativeRows.All(row => row.LeaseId != reusedRow.LeaseId) && !remoteLeases.Contains(again.LeaseId),
            "reusing a numeric native slot creates fresh native and remote lease identities");
        await receiver.ReleaseAsync(Lease(again.LeaseId!), default);
        check(account.Observation.SnapshotV2().Snapshot.Rows.Single(row => row.RowId == reusedLink.RowId) is
            { Release: "confirmed", Terminal: null }, "unused linked preparation release invents no generation terminal");

        adapter.FailNext = true;
        var failed = false;
        try { await receiver.PrepareAsync(Input(), default); }
        catch (InvalidOperationException error) when (error.Message == "fixture preparation failure") { failed = true; }
        check(failed && account.Snapshot().Entries.Length == 0 && adapter.Pool.AvailableCount == 2 &&
            WorkerInferenceInvocation.Current is null,
            "receiver preparation exception restores scope and releases only its fixture leases");

        using (var cancellation = new CancellationTokenSource())
        {
            adapter.BlockNext = true;
            var pending = receiver.PrepareAsync(Input(), cancellation.Token);
            await adapter.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var cancelled = false;
            try { await pending; } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && account.Snapshot().Entries.Length == 0 && adapter.Pool.AvailableCount == 2 &&
                WorkerInferenceInvocation.Current is null,
                "cancelled receiver preparation unwinds the exact parent scope and fixture ownership");
        }

        var bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        var image = new ImageContentPart(new("image", 1, "image/png", "fixture.png", bytes.Length), Width: 1, Height: 1);
        var imageRequest = request with { Messages = [new("user", [new TextContentPart("fixture image"), image])] };
        var imagePrepared = Prepared(await receiver.PrepareAsync(CerebralJson.Element(new RemotePrepareRequestV2(
            RemotePreparedProtocol.ImageVersion, imageRequest, binding, [new(0, 1, "image/png", 1, 1, bytes)])), default));
        check(adapter.ImagePreparations == 1 && account.Observation.SnapshotV2().Links.Any(link =>
            link.State == "observed" && link.InboundPreparation?.PreparedLeaseId == imagePrepared.LeaseId),
            "actual receiver image delegate runs inside the same explicit native parent scope");
        await receiver.ReleaseAsync(Lease(imagePrepared.LeaseId!, RemotePreparedProtocol.ImageVersion), default);
        check(account.Observation.SnapshotV2(DateTimeOffset.UtcNow.AddMinutes(6)) is { Links.Length: 0, Snapshot.Rows.Length: 0 },
            "released annotations expire with their rows rather than forming a second history");

        var owner = new ModelExecutionAccounting(coverageComplete: true);
        owner.Observation.ConfigureIssuer("owner"); owner.Observation.StartScheduler();
        var parent = owner.Begin(ModelOwnershipKind.InboundReservation, "model"); parent.Bind(leaseId: "inbound-lease");
        var tuple = new InferenceInboundPreparation(owner.ProcessGenerationId, parent.Id, "inbound-lease");
        var coordinate = new InferenceExecutionCoordinate("execution", "profile", 1);
        using (WorkerInferenceInvocation.Enter(new("host-correlation", "task-generation", coordinate)))
        {
            try
            {
                using var scope = WorkerInferenceInvocation.EnterInboundPreparation(tuple);
                await Task.Yield();
                check(WorkerInferenceInvocation.Current is { CorrelationId: "host-correlation", Execution: var actual } &&
                    actual == coordinate && WorkerInferenceInvocation.Current.InboundPreparation == tuple,
                    "inbound scope preserves real host invocation and flows across await");
                throw new IOException("fixture scope unwind");
            }
            catch (IOException) { }
            check(WorkerInferenceInvocation.Current is { CorrelationId: "host-correlation", InboundPreparation: null },
                "nested inbound scope restores the previous invocation on exception");
        }
        var other = new ModelExecutionAccounting(coverageComplete: true);
        other.Observation.ConfigureIssuer("other"); other.Observation.StartScheduler();
        using (WorkerInferenceInvocation.EnterInboundPreparation(tuple))
        {
            var crossLedger = other.Begin(ModelOwnershipKind.LocalPreparation, "model");
            check(other.Observation.SnapshotV2().Links.Single() is { State: "unavailable", InboundPreparation: null } &&
                !other.Observation.Snapshot().Partial,
                "cross-ledger parent fails closed only in supplemental coverage");
            crossLedger.Complete();
        }
        using (WorkerInferenceInvocation.EnterInboundPreparation(tuple with { PreparedLeaseId = "wrong-lease" }))
        {
            var invalid = owner.Begin(ModelOwnershipKind.LocalPreparation, "model");
            check(owner.Observation.SnapshotV2().Links.Single() is { State: "unavailable", InboundPreparation: null } &&
                !owner.Observation.Snapshot().Partial, "mismatched exact parent lease does not change frozen v1 coverage");
            invalid.Complete();
        }
        ModelExecutionAccounting.ModelOwnership quarantined;
        using (WorkerInferenceInvocation.EnterInboundPreparation(tuple))
        {
            quarantined = owner.Begin(ModelOwnershipKind.LocalPreparation, "model");
            quarantined.Bind(instanceId: "quarantined-instance"); quarantined.NativeSlot("native-lease", 0);
            quarantined.Phase(ModelOwnershipPhase.Generating, true); quarantined.Unknown("native-slot-quarantined");
        }
        parent.Complete();
        var unresolved = owner.Observation.SnapshotV2(DateTimeOffset.UtcNow.AddDays(1));
        check(unresolved.Links.Single().InboundPreparation == tuple &&
            unresolved.Snapshot.Rows.Single() is { Phase: "quarantined", Release: "unobserved" },
            "exact linked quarantine survives parent history expiry without inferring release");
        owner.CompleteStoppedInstance("quarantined-instance");

        var output = new ModelExecutionAccounting(coverageComplete: true);
        output.Observation.ConfigureIssuer("caller"); output.Observation.StartScheduler();
        var outbound = output.Begin(ModelOwnershipKind.OutboundPreparation, "model");
        outbound.Bind(instanceId: "remote-instance", reservationId: "reservation", workerInstallationId: "remote-installation", leaseId: "remote-lease");
        var wire = JsonSerializer.SerializeToElement(CommandDispatcher.ReadCapturedInferenceObservationResponse(
            output.Observation, CerebralJson.Element(new { }), default), CerebralJson.Options);
        var expectedSnapshot = new[] { "schema", "issuingInstallationId", "ownerConnectionRegistrationFence",
            "ownerConnectionRegistrationFenceState", "ownerConnectionRegistrationFenceReason", "observerGenerationId", "revision",
            "sampledAt", "accountingGenerationId", "accountingCoverageComplete", "state", "partial", "truncated", "maximumRows",
            "retainedCurrentRows", "reasons", "counts", "modelCoverage", "models", "rows", "remoteState", "eventDeliveryState",
            "executionAdmissionOpen", "modelAdmissionOpen", "admissionSource", "semantics" };
        var expectedRow = new[] { "rowId", "accountingEntryId", "accountingGenerationId", "accountingRevision", "originInstallationId",
            "kind", "modelId", "instanceId", "correlationId", "attemptId", "purpose", "execution", "executionAttribution",
            "accountingLeaseId", "reservationId", "leaseId", "nativeSlotId", "phase", "cancellationRequested", "uncertaintyCode",
            "budget", "terminal", "cache", "progress", "processedInputTokens", "outputTokens", "progressSource", "release",
            "releaseProof", "releasedAt", "observedAt" };
        check(wire.GetProperty("schema").GetString() == "cerebral.inference-ownership/1" &&
            wire.EnumerateObject().Select(field => field.Name).SequenceEqual(expectedSnapshot) &&
            wire.GetProperty("rows")[0].EnumerateObject().Select(field => field.Name).SequenceEqual(expectedRow) &&
            !wire.GetRawText().Contains("remoteInstallationId", StringComparison.Ordinal),
            "empty selector retains exact frozen Main v1 snapshot and row field sets despite retained links");
        var selected = (InferenceObservationLinksSnapshot)CommandDispatcher.ReadCapturedInferenceObservationResponse(
            output.Observation, CerebralJson.Element(new { schemaVersion = 2 }), default);
        check(selected.Schema == "cerebral.inference-ownership/2" &&
            selected.Snapshot.AccountingGenerationId == output.ProcessGenerationId &&
            selected.Links.Single() is { State: "observed", RemoteInstallationId: "remote-installation", InboundPreparation: null } &&
            selected.Links.Single().RowId == selected.Snapshot.Rows.Single().RowId &&
            selected.Snapshot.Rows.Single() is { AccountingLeaseId: "remote-lease", LeaseId: null, NativeSlotId: null },
            "explicit v2 projects the captured remote installation and exact transport lease without a native slot claim");
        var beforeInvalid = output.Observation.Snapshot().Revision;
        var rejected = 0;
        foreach (var invalid in new[] { "null", "[]", "true", "{\"schemaVersion\":1}", "{\"schemaVersion\":3}",
            "{\"schemaVersion\":\"2\"}", "{\"schemaVersion\":2.5}", "{\"schemaVersion\":2147483648}",
            "{\"schemaVersion\":2,\"schemaVersion\":2}", "{\"schemaVersion\":2,\"extra\":true}", "{\"unknown\":2}" })
        {
            using var document = JsonDocument.Parse(invalid);
            try { CommandDispatcher.ReadCapturedInferenceObservationResponse(output.Observation, document.RootElement, default); }
            catch (ArgumentException) { rejected++; }
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var refused = false;
            try { CommandDispatcher.ReadCapturedInferenceObservationResponse(output.Observation,
                CerebralJson.Element(new { schemaVersion = 2 }), cancelled.Token); }
            catch (OperationCanceledException) { refused = true; }
            check(rejected == 11 && refused && output.Observation.Snapshot().Revision == beforeInvalid,
                "invalid duplicate expanded and cancelled v2 selectors reject without capture mutation");
        }
        outbound.Bind(workerInstallationId: "different-installation"); outbound.Phase(ModelOwnershipPhase.Generating);
        check(output.Observation.SnapshotV2().Links.Single() is { State: "unavailable", RemoteInstallationId: null } &&
            !output.Observation.Snapshot().Partial, "changed remote identity cannot be recertified by later accounting updates");
        outbound.Complete();

        var payload = new InferenceObservationRegistry("payload-generation", true);
        payload.ConfigureIssuer("payload-owner"); payload.StartScheduler();
        for (var index = 0; index < 128; index++)
        {
            var value = new string('e', 252) + index.ToString("D4");
            var invocation = new InferenceInvocation(new string('c', 256), "task-generation",
                new(new string('x', 256), new string('p', 256), 1, new string('q', 256),
                    new string('r', 256), new string('s', 256), new string('t', 256)));
            payload.Accounting(Entry(value) with { Kind = ModelOwnershipKind.OutboundPreparation,
                WorkerInstallationId = new string('w', 256), InstanceId = new string('i', 256),
                LeaseId = new string('l', 256), ReservationId = new string('v', 256) }, index + 1, invocation);
        }
        var bounded = payload.SnapshotV2();
        check(bounded.Snapshot.RetainedCurrentRows == 128 && bounded.Links.Length <= 128 &&
            JsonSerializer.SerializeToUtf8Bytes(bounded, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length <= InferenceObservationRegistry.MaximumPayloadBytes &&
            bounded.Links.All(link => bounded.Snapshot.Rows.Any(row => row.RowId == link.RowId)) &&
            bounded.Truncated && bounded.Partial,
            "v2 combined envelope byte and row caps keep coherent selected annotations without evicting ownership");
    }

    private sealed class InboundLinkAdapter : IModelRuntimeAdapter
    {
        internal ModelExecutionAccounting Accounting { get; set; } = null!;
        internal LlamaSlotPool Pool { get; } = new(2);
        internal bool WaitForPair = true, FailNext, BlockNext;
        internal int ImagePreparations;
        internal System.Collections.Concurrent.ConcurrentDictionary<string, string> NativeEntries { get; } = new(StringComparer.Ordinal);
        internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource pair = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrived;
        public ModelAdapterDescriptor Descriptor { get; } = new("fixture", "1", ["text", "image"]);
        public async Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken token)
        {
            await Task.Yield();
            if (WaitForPair)
            {
                if (Interlocked.Increment(ref arrived) == 2) pair.TrySetResult();
                await pair.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
            var ownership = Accounting.Begin(ModelOwnershipKind.LocalPreparation, request.ModelId);
            NativeEntries[request.Messages[0].Content.OfType<TextContentPart>().Single().Text] = ownership.Id;
            using var cancellation = ownership.ObserveCancellation(token);
            LlamaSlotPool.Lease? slot = null;
            try
            {
                slot = await Pool.AcquireAsync(ownership.Complete, token);
                ownership.Bind(instanceId: "fixture-instance", leaseId: slot.LeaseId);
                ownership.NativeSlot(slot.LeaseId, slot.Id); ownership.Phase(ModelOwnershipPhase.Preparing, true);
                if (FailNext) { FailNext = false; throw new InvalidOperationException("fixture preparation failure"); }
                if (BlockNext)
                {
                    BlockNext = false; Blocked.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                return new AccountingPreparedGeneration(new FakePrepared(slot, true,
                    new(ModelGenerationFinish.Stop, "fixture completion", 3, 100)
                    { PromptCache = new(100, 40, 60, 12) }), ownership, cancellationToken: token);
            }
            catch { if (slot is null) ownership.Complete(); else slot.Dispose(); throw; }
        }
        internal Task<IPreparedModelGeneration> PrepareMultimodalAsync(ModelGenerationRequest request,
            IReadOnlyList<RemotePreparedImageInput> images, CancellationToken token)
        { ImagePreparations++; return PrepareAsync(request, token); }
    }

    private static async Task SchedulerAndDispatcherAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-inference-observation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new StateStore(root);
            var host = new HostSettings { InstallationId = "11111111111111111111111111111111", AccessKey = "", Coordinator = true };
            store.Put("settings", "host", host);
            var account = ModelExecutionAccounting.For(store, coverageComplete: true);
            account.Observation.ConfigureIssuer(host.InstallationId);
            var configuration = new Configuration(store); var events = new EventHub();
            await using var runtime = new ModelRuntime(store, () => throw new InvalidOperationException("read must not get settings"), events);
            runtime.InitializeInferenceObservation();
            using var scheduler = new JobScheduler(store, configuration, null!, null!, events, NullLogger<JobScheduler>.Instance);
            var profile = new AgentProfile { Id = "shared-profile", Revision = 4, WorkerId = "local",
                ModelId = "fixture-model", ModelWorkerId = "local" };
            var binding = new WorkerExecutionSnapshot.ModelBinding(profile.ModelId, "fixture-path", null, [], 16384, 2, 0);
            var accepted = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version, profile, "", "", binding,
                [], [], [], [], false, "fixture-accepted-routing", [], [], null);
            var submission = new WorkflowSubmission("native", "conversation", profile.Id, "fixture.definition", "1", "", "",
                WorkflowContract.EmptyObject, CerebralJson.Element(accepted));
            using var workflowToken = new CancellationTokenSource();
            using var jobToken = new CancellationTokenSource();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(JobScheduler).GetMethod("ObserveAcceptedWorkflow", flags)!.Invoke(scheduler,
                new object[] { "workflow:native", workflowToken, submission });
            var job = new AgentJob { Id = "legacy", ProfileId = profile.Id, WorkerId = "local",
                Profile = new AgentProfile { Id = profile.Id, Revision = 5, WorkerId = "local", ModelId = profile.ModelId, ModelWorkerId = "local" } };
            typeof(JobScheduler).GetMethod("ObserveAcceptedJob", flags)!.Invoke(scheduler, new object[] { job, jobToken });
            var counts = account.Observation.Snapshot();
            check(counts.Counts.CompleteExecutions == 2 && counts.Counts.CompleteWorkingProfileIdentities == 1 && counts.Rows.Length == 0,
                "actual native-workflow and legacy-job capture seams count two scheduler executions before any preparer row");
            profile.Revision = 99;
            check(account.Observation.Snapshot().Counts.CompleteExecutions == 2,
                "later mutable profile changes do not re-read or replace accepted capture");

            var commands = new CommandDispatcher(store, configuration, runtime, scheduler, null!, events);
            var lifecycle = (SemaphoreSlim)typeof(ModelRuntime).GetField("lifecycle", flags)!.GetValue(runtime)!;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseStore = new ManualResetEventSlim();
            var locked = Task.Run(() => store.Locked(() =>
            { entered.SetResult(); if (!releaseStore.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("fixture store hold"); return true; }));
            await entered.Task; await lifecycle.WaitAsync();
            try
            {
                var observed = await commands.ExecuteAsync(new("metrics.inference.status", CerebralJson.Element(new { })))
                    .WaitAsync(TimeSpan.FromSeconds(2));
                check(observed.Success && observed.Data.GetProperty("counts").GetProperty("completeExecutions").GetInt32() == 2,
                    "real owner dispatcher snapshot succeeds while store and runtime lifecycle are held");
                check(configuration.GetInvocationCount == 0, "owner capture read invokes no configuration normalization or settings callback");
                var linked = await commands.ExecuteAsync(new("metrics.inference.status", CerebralJson.Element(new { schemaVersion = 2 })))
                    .WaitAsync(TimeSpan.FromSeconds(2));
                check(linked.Success && linked.Data.GetProperty("schema").GetString() == "cerebral.inference-ownership/2" &&
                    linked.Data.GetProperty("snapshot").GetProperty("counts").GetProperty("completeExecutions").GetInt32() == 2 &&
                    configuration.GetInvocationCount == 0,
                    "v2 real owner dispatcher remains passive while store and runtime lifecycle are held");
            }
            finally { lifecycle.Release(); releaseStore.Set(); await locked; }
            var admin = new AgentProfile { Id = "admin", Role = "admin", Capabilities = ["administration"] };
            store.Put("agent", admin.Id, admin);
            var refused = await commands.ExecuteAsync(new("metrics.inference.status", CerebralJson.Element(new { }), admin.Id));
            check(!refused.Success && refused.Error?.Contains("owner interface", StringComparison.Ordinal) == true,
                "administration agent cannot read owner-only inference capture");
            var refusedV2 = await commands.ExecuteAsync(new("metrics.inference.status", CerebralJson.Element(new { schemaVersion = 2 }), admin.Id));
            check(!refusedV2.Success && refusedV2.Error?.Contains("owner interface", StringComparison.Ordinal) == true,
                "schema v2 cannot expand the existing owner-only diagnostic authority");
            var expanded = await commands.ExecuteAsync(new("metrics.inference.status", CerebralJson.Element(new { owners = new[] { "remote" } })));
            check(!expanded.Success, "caller cannot expand local owner scope or command payload");
            typeof(JobScheduler).GetMethod("ObserveRetiredExecution", flags)!.Invoke(scheduler,
                new object[] { "workflow:native", workflowToken });
            check(account.Observation.Snapshot().Counts.CompleteExecutions == 1,
                "exact native workflow retirement leaves legacy capture present");
            typeof(JobScheduler).GetMethod("ObserveRetiredExecution", flags)!.Invoke(scheduler, new object[] { job.Id, jobToken });
            check(account.Observation.Snapshot().Counts.CompleteExecutions == 0,
                "both scheduler surfaces retire independently without inferring provider release");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (Path.GetDirectoryName(full) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(full).StartsWith("cerebral-inference-observation-", StringComparison.Ordinal))
                throw new InvalidOperationException("unsafe fixture cleanup");
            Directory.Delete(full, true);
        }
    }

    private static async Task OwnedLifecycleOrderingAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-inference-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ManagedProcess? ownedA = null, ownedB = null;
        ModelRuntime? ownedRuntime = null;
        var pidA = 0; var pidB = 0; long ticksA = 0, ticksB = 0;
        Exception? failure = null;
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("test host path unavailable");
            var childArguments = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? new[] { typeof(InferenceObservationTests).Assembly.Location, "--inference-lifecycle-child" }
                : new[] { "--inference-lifecycle-child" };
            // These are separately launched, job/group-owned non-model children. Never wrap this
            // test process as a stoppable ManagedProcess; the actual runtime cleanup will terminate them.
            var childA = ownedA = ManagedProcess.Start(executable, childArguments, root);
            pidA = childA.Process.Id; ticksA = childA.Process.StartTime.ToUniversalTime().Ticks;
            var childB = ownedB = ManagedProcess.Start(executable, childArguments, root);
            pidB = childB.Process.Id; ticksB = childB.Process.StartTime.ToUniversalTime().Ticks;
            var readyA = await childA.Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var readyB = await childB.Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            check(readyA == "inference-lifecycle-ready" && readyB == readyA &&
                pidA != Environment.ProcessId && pidB != Environment.ProcessId && pidA != pidB,
                "lifecycle fixture owns two separate non-model child identities");

            var store = new StateStore(root);
            const string modelId = "lifecycle-fixture";
            store.Put("model", modelId, new ModelRecord { Id = modelId, State = "loaded",
                Path = Path.Combine(root, "synthetic-not-a-model.fixture"), ParallelRequests = 2 });
            _ = ModelExecutionAccounting.For(store, coverageComplete: true);
            var runtime = ownedRuntime = new ModelRuntime(store, () => throw new InvalidOperationException("lifecycle fixture must not read settings"), new EventHub());
            runtime.InitializeInferenceObservation();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            object Invoke(string name, params object[] args) =>
                typeof(ModelRuntime).GetMethod(name, flags)!.Invoke(runtime, args)!;
            T Field<T>(object value, string name) =>
                (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value)!;
            object Instance(ManagedProcess child)
            {
                var type = typeof(ModelRuntime).GetNestedType("Instance", BindingFlags.NonPublic)!;
                return type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Single().Invoke([child, new HttpClient(), 2, null, null, TimeProvider.System]);
            }
            void Own(object instance, ManagedProcess child)
            {
                var ownership = runtime.Accounting.Begin(ModelOwnershipKind.LocalContext, modelId, requiresAdmission: false);
                ownership.Bind(instanceId: Field<string>(instance, "Id"), processId: child.Process.Id);
                ownership.Phase(ModelOwnershipPhase.Ready, effectStarted: true);
                instance.GetType().GetField("Ownership")!.SetValue(instance, ownership);
            }
            var first = Instance(childA); var replacement = Instance(childB);
            var firstId = Field<string>(first, "Id"); var replacementId = Field<string>(replacement, "Id");
            Own(first, childA); Own(replacement, childB);
            Invoke("InstallOwnerObservedInstance", modelId, first);
            var firstPool = Field<LlamaSlotPool>(first, "Slots");
            var firstCallback = (Action)typeof(LlamaSlotPool).GetField("poolObservation", flags)!.GetValue(firstPool)!;
            Invoke("RemoveOwnerObservedInstance", modelId, first);
            Invoke("InstallOwnerObservedInstance", modelId, replacement);
            firstCallback();
            var afterStale = runtime.Accounting.Observation.Snapshot();
            check(!childA.Process.HasExited && !Field<bool>(first, "Stopped") &&
                afterStale.Models.Length == 1 && afterStale.Models[0].InstanceId == replacementId,
                "live old-instance callback after replacement cannot recreate retired inventory");

            bool? staleStopInventory = null, staleStopChildAlive = null;
            firstPool.ObserveChanges(() =>
            {
                firstCallback();
                staleStopChildAlive = !childA.Process.HasExited && !Field<bool>(first, "Stopped");
                var page = runtime.Accounting.Observation.Snapshot();
                staleStopInventory = page.Models.Length == 1 && page.Models[0].InstanceId == replacementId;
            });
            await ((Task)Invoke("StopAsync", modelId, first)).WaitAsync(TimeSpan.FromSeconds(15));
            check(staleStopInventory == true && staleStopChildAlive == true,
                "actual stale StopAsync pool-completion edge preserves replacement before old child stops");
            check(ExactGone(pidA, ticksA), "actual StopAsync confirms the separately owned old child exited");

            var replacementPool = Field<LlamaSlotPool>(replacement, "Slots");
            var replacementCallback = (Action)typeof(LlamaSlotPool).GetField("poolObservation", flags)!.GetValue(replacementPool)!;
            bool? removedInventory = null, childAliveBeforeStop = null, ownershipRetainedBeforeStop = null;
            replacementPool.ObserveChanges(() =>
            {
                replacementCallback();
                childAliveBeforeStop = !childB.Process.HasExited && !Field<bool>(replacement, "Stopped");
                var page = runtime.Accounting.Observation.Snapshot();
                removedInventory = page.Models.Length == 0;
                ownershipRetainedBeforeStop = runtime.Accounting.Snapshot().LocalContexts == 1;
            });
            await runtime.UnloadAsync(modelId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            check(removedInventory == true && childAliveBeforeStop == true && ownershipRetainedBeforeStop == true,
                "actual UnloadAsync removal and Slots.Complete cannot resurrect inventory before child-stop confirmation");
            check(ExactGone(pidB, ticksB) && runtime.Accounting.Snapshot().LocalContexts == 0 &&
                runtime.Accounting.Observation.Snapshot().Models.Length == 0 &&
                store.Get<ModelRecord>("model", modelId)?.State == "unloaded",
                "actual unload confirms child cleanup and retires only its owned contexts");

            lifecycleEvidence.Add(new { tier = "actual self-owned non-model OS-process lifecycle; synthetic model metadata",
                oldChildPid = pidA, oldChildStartUtcTicks = ticksA.ToString(System.Globalization.CultureInfo.InvariantCulture),
                replacementChildPid = pidB, replacementChildStartUtcTicks = ticksB.ToString(System.Globalization.CultureInfo.InvariantCulture),
                oldExactChildStopped = true, replacementExactChildStopped = true,
                actualPrivateStopAsync = true, actualPublicUnloadAsync = true,
                staleCallbackRejectedWhileOldChildAlive = true, poolCompleteObservedBeforeChildStopped = true,
                modelLoadPrepareGenerateCalls = 0, providerNetworkCalls = 0 });
        }
        catch (Exception error) { failure = error; }
        finally
        {
            // Dispose alone requests termination without waiting. Keep owned handles available until
            // their exact children settle, and retain the body failure if any cleanup also fails.
            var cleanupErrors = new List<Exception>();
            if (ownedRuntime is not null)
                try { await ownedRuntime.DisposeAsync(); } catch (Exception error) { cleanupErrors.Add(error); }
            async Task ConfirmChildAsync(ManagedProcess? child, int pid, long ticks)
            {
                if (child is null) return;
                try
                {
                    // Runtime stop may already have disposed Process after confirmed job cleanup.
                    if ((pid <= 0 || ticks <= 0 || !ExactGone(pid, ticks)) &&
                        !await child.StopAndConfirmAsync(TimeSpan.FromSeconds(10)))
                        throw new IOException("Owned lifecycle fixture child cleanup was not confirmed.");
                }
                catch (Exception error) { cleanupErrors.Add(error); }
                finally
                {
                    try { child.Dispose(); } catch (Exception error) { cleanupErrors.Add(error); }
                }
            }
            await ConfirmChildAsync(ownedA, pidA, ticksA);
            await ConfirmChildAsync(ownedB, pidB, ticksB);
            try
            {
                SqliteConnection.ClearAllPools();
                var full = Path.GetFullPath(root);
                if (Path.GetDirectoryName(full) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                    !Path.GetFileName(full).StartsWith("cerebral-inference-lifecycle-", StringComparison.Ordinal))
                    throw new InvalidOperationException("unsafe lifecycle fixture cleanup");
                if (ownedA is not null && !ExactGone(pidA, ticksA) || ownedB is not null && !ExactGone(pidB, ticksB))
                    throw new IOException("Owned lifecycle fixture children remain; temporary directory retained.");
                Directory.Delete(full, true);
            }
            catch (Exception error) { cleanupErrors.Add(error); }
            if (cleanupErrors.Count != 0)
            {
                if (failure is not null) cleanupErrors.Insert(0, failure);
                failure = new AggregateException("Lifecycle fixture body or cleanup failed.", cleanupErrors);
            }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

        static bool ExactGone(int pid, long ticks)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.HasExited || process.StartTime.ToUniversalTime().Ticks != ticks;
            }
            catch (ArgumentException) { return true; }
        }
    }

    private static ModelOwnershipEntry Entry(string id) => new(id, ModelOwnershipKind.LocalPreparation,
        ModelOwnershipPhase.Queued, "model", null, null, null, null, null, false, null);
    private sealed class FakePrepared(LlamaSlotPool.Lease lease, bool releaseDuringGeneration,
        ModelGenerationResult result, Action? dispatched = null) : IPreparedModelGeneration
    {
        public PreparedModelMetadata Metadata { get; } = new("fixture-instance", "fixture", "1", "model", 100, 16384, 512, LlamaProtocol.CountBasis)
            { SafetyReserveTokens = 128 };
        public Task<ModelGenerationResult> GenerateAsync(Action<string>? text, CancellationToken token)
        { dispatched?.Invoke(); if (releaseDuringGeneration) lease.Dispose(); return Task.FromResult(result); }
        public ValueTask DisposeAsync() { lease.Dispose(); return ValueTask.CompletedTask; }
    }
}
