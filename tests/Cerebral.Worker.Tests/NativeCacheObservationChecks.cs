using System.Text.Json;
using Cerebral.Contracts.Models;

namespace Cerebral.Worker;

// Pure/injected source checks. No provider, process, model, service, environment mutation or filesystem calls.
internal static class NativeCacheObservationChecks
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool condition, string name)
        { count++; if (!condition) throw new InvalidOperationException("Native cache observation check: " + name); }
        var epoch = 1L;
        var at = DateTimeOffset.UnixEpoch;
        var owner = new NativeCacheOwner("model", "instance", "runtime", epoch, new string('a', 64),
            NativeExecutableIdentity.Unobserved("unobserved"));
        NativeCacheBinding Binding(string lease, int slot = 0) =>
            new("accounting:" + lease, "accounting", lease, "attempt-" + lease, "model", "instance",
                lease, slot, "task-generation", "local-host-invocation");
        JsonElement Input(int n, int change = -1)
        {
            var ids = Enumerable.Range(0, n).ToArray();
            if (change >= 0) ids[change] += 10000;
            return JsonSerializer.SerializeToElement(ids);
        }
        ModelGenerationResult Stop() => new(ModelGenerationFinish.Stop, "never-serialize-model-result");
        using var observation = new NativeCacheObservation(owner, 1, () => epoch, () => at,
            Enumerable.Repeat((byte)7, 32).ToArray());
        var captured = new List<NativeCacheGenerationObservation>();
        NativeCacheGeneration Begin(string lease, JsonElement input, bool supported = true,
            NativeCacheBinding? binding = null, CancellationToken token = default) =>
            observation.Begin(0, lease, binding ?? Binding(lease), captured.Add, input, input.GetArrayLength(), supported, token);
        void Finish(NativeCacheGeneration generation, ModelGenerationResult? result = null,
            bool sent = true, bool ack = true, bool idle = true, bool live = true, bool cancel = false) =>
            observation.Complete(generation, result ?? Stop(), sent, ack, idle, live, cancel,
                NativeCacheProviderEvidence.Missing);

        var first = Begin("one", Input(137));
        Check(first.Comparison.State == "no-baseline", "first input has no invented baseline");
        Finish(first);
        var append = Begin("two", Input(400));
        Check(append.Comparison.PreviousInputIsPrefixOfCurrent == true, "non-band full boundary matches appended prefix");
        Check(append.Comparison.CommonPrefixLowerBoundTokens == 137 &&
            append.Comparison.CommonPrefixUpperBoundTokens == 137, "exact old-full prefix bound");
        Check(append.Comparison.SamePreparedInput == false, "append is not equal full input");
        Finish(append);
        // No outer terminal/retirement callback is delivered; native pre-return settlement is sufficient.
        var equal = Begin("three", Input(400));
        Check(equal.Comparison.SamePreparedInput == true, "next handoff sees native settlement before outer terminal");
        Check(equal.Comparison.BaselineGenerateSequence == 2 && equal.Comparison.BaselineLeaseId == "two",
            "exact previous generation/lease attribution");
        Finish(equal);

        var changed = Begin("four", Input(400, 100));
        Check(changed.Comparison.CommonPrefixLowerBoundTokens == 64 &&
            changed.Comparison.CommonPrefixUpperBoundTokens == 255, "difference inside bounded band");
        var largerCache = Stop() with { PromptCache = new(400, 300, 100) };
        Finish(changed, largerCache);
        Check(captured[^1].Comparison.State == "observed" &&
            largerCache.PromptCache!.CachedTokens is { } providerCached &&
            captured[^1].Comparison.CommonPrefixUpperBoundTokens is { } prefixUpper && providerCached > prefixUpper,
            "provider cached count is not bounded by prior prepared-input prefix");
        Check(captured[^1].ProviderEvidence.RetentionState == "unobserved" &&
            captured[^1].ProviderEvidence.NativeTaskId is null, "retention/task evidence not prerequisite to comparison");
        var changedEqual = Begin("five", Input(400, 100));
        Check(changedEqual.Comparison.SamePreparedInput == true, "cache counters cannot poison prepared-input history");
        Finish(changedEqual);

        var shorter = Begin("six", Input(100));
        Check(shorter.Comparison.PreviousInputIsPrefixOfCurrent == false &&
            shorter.Comparison.SamePreparedInput == false, "shorter input cannot contain prior full input");
        Finish(shorter);
        at += TimeSpan.FromMinutes(6);
        var expired = Begin("seven", Input(100));
        Check(expired.Comparison.State == "no-baseline" && expired.Comparison.Reason == "baseline-expired",
            "lazy expiry unavailable, not guessed equality");
        Finish(expired);
        epoch++;
        var newEpoch = Begin("eight", Input(100));
        Check(newEpoch.Comparison.State == "unavailable" && newEpoch.Comparison.Reason == "runtime-epoch-changed",
            "epoch change invalidates old input");
        Finish(newEpoch);
        var sameEpoch = Begin("nine", Input(100));
        Check(sameEpoch.Comparison.SamePreparedInput == true, "new exact epoch may establish new native input");
        Finish(sameEpoch);

        var unsupported = Begin("ten", Input(100), supported: false);
        Check(unsupported.Comparison.State == "unavailable", "unsupported native use downgraded");
        Finish(unsupported);
        var afterUnsupported = Begin("eleven", Input(100));
        Check(afterUnsupported.Comparison.SamePreparedInput is null, "unsupported use leaves no eligible stale baseline");
        Finish(afterUnsupported);
        var bad = Binding("twelve") with { AttemptId = "", InstanceId = "other" };
        var unbound = Begin("twelve", Input(100), binding: bad);
        Check(unbound.Comparison.Reason == "exact-entry-binding-unavailable", "missing exact row binding");
        Finish(unbound);
        var afterUnbound = Begin("thirteen", Input(100));
        Check(afterUnbound.Comparison.SamePreparedInput is null, "invalid entry cannot promote");
        Finish(afterUnbound);

        foreach (var failure in new[] { "not-sent", "no-ack", "no-idle", "exited", "cancel", "error", "unknown" })
        {
            var failed = Begin("failure-" + failure, Input(100));
            Finish(failed,
                failure == "error" ? Stop() with { Error = "synthetic-error" } :
                failure == "unknown" ? new(ModelGenerationFinish.Unknown, "") : Stop(),
                sent: failure != "not-sent", ack: failure != "no-ack", idle: failure != "no-idle",
                live: failure != "exited", cancel: failure == "cancel");
            var next = Begin("after-" + failure, Input(100));
            Check(next.Comparison.SamePreparedInput is null, "no promotion on " + failure);
            Finish(next);
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var noHash = Begin("cancelled-hash", Input(100), token: cancelled.Token);
            Check(noHash.Comparison.Reason == "prefix-observation-unavailable", "hash cancellation is diagnostic unavailable");
            Finish(noHash);
        }
        var afterHashFailure = Begin("after-hash-failure", Input(100));
        Check(afterHashFailure.Comparison.SamePreparedInput is null, "hash failure cannot establish baseline");
        Finish(afterHashFailure);
        var future = Begin("future", Input(100));
        epoch++;
        Finish(future);
        Check(captured[^1].Comparison.Reason == "source-changed" &&
            captured[^1].Owner.ObservationState == "unavailable", "epoch change during generation cannot publish current eligibility");

        var stopLate = Begin("late", Input(100));
        observation.Dispose();
        Finish(stopLate);
        Check(captured[^1].Comparison.State == "unavailable" &&
            captured[^1].Owner.ObservationReason == "instance-stopped" &&
            !captured[^1].NativeStopConfirmed, "late publication cannot resurrect stopped instance");
        var wire = JsonSerializer.Serialize(captured);
        Check(!wire.Contains("never-serialize-model-result", StringComparison.Ordinal) &&
            !wire.Contains("Digests", StringComparison.Ordinal) && !wire.Contains("\"Values\"", StringComparison.Ordinal) &&
            !wire.Contains("\"key\"", StringComparison.Ordinal), "record output excludes content/digests/key");

        var zero = new PromptCacheMetrics(10, 0, 10);
        NativeCacheProviderEvidence Evidence(string json, PromptCacheMetrics? metrics = null)
        {
            using var document = JsonDocument.Parse(json);
            return NativeCacheObservation.Evidence(document.RootElement, metrics ?? zero, null, false);
        }
        Check(Evidence("{}").CachedSource is null && Evidence("{}").ProcessedSource is null,
            "accepted prior metric cannot invent current origin");
        var explicitZero = Evidence("""{"timings":{"cache_n":0,"prompt_n":10}}""");
        Check(explicitZero.CachedSource == "timings.cache_n" && explicitZero.ProcessedSource == "timings.prompt_n",
            "explicit matching current zero has origin");
        Check(Evidence("""{"timings":{"cache_n":1,"prompt_n":10}}""").CachedSource is null,
            "mismatching current cache origin is unknown");
        Check(Evidence("""{"timings":{"cache_n":0,"prompt_n":9}}""").ProcessedSource is null,
            "mismatching current processed origin is unknown");
        Check(Evidence("""{"usage":{"prompt_tokens_details":{"cached_tokens":0}}}""").CachedSource ==
            "usage.prompt_tokens_details.cached_tokens", "explicit matching usage fallback only");
        Check(Evidence("""{"usage":{"prompt_tokens_details":{"cached_tokens":"0"}}}""").CachedSource is null,
            "malformed usage origin is unknown");
        Check(Evidence("""{"timings":{"cache_n":0},"usage":{"prompt_tokens_details":{"cached_tokens":1}}}""").CachedSource is null,
            "conflicting current origins are unknown");
        Check(Evidence("""{"timings":{"prompt_n":10}}""").CachedSource is null,
            "processed count does not derive cached origin");
        Check(Evidence("""{"timings":{"cache_n":0,"prompt_n":10}}""", zero with { Diagnostic = "synthetic" }).CachedSource is null,
            "invalid accepted metric never names origin");

        var fileIdentity = new NativeExecutableIdentity("launch-file-only", "main-executable", new string('b', 64),
            10, null, null, "read-handle-held-through-spawn", "unobserved", "owned-child-not-bound");
        var image = Path.GetFullPath("native-fixture.exe");
        var matched = NativeExecutableCapture.Bind(fileIdentity, image, image, 123, 456, false);
        Check(matched.State == (OperatingSystem.IsWindows() ? "observed-main-executable" : "launch-file-only"),
            "owned child identity has platform-scoped basis");
        Check(matched.DependencyIdentityState == "unobserved" && matched.Scope == "main-executable",
            "main executable never implies backend identity");
        Check(NativeExecutableCapture.Bind(fileIdentity, image, image, 123, 456, true).State == "launch-file-only",
            "exited child not observed implementation");
        Check(NativeExecutableCapture.Bind(fileIdentity, image, image + ".other", 123, 456, false).State == "launch-file-only",
            "wrong image not observed implementation");

        PoolChecks(owner, Check);
        var observedInstances = new List<NativeCacheObservation>();
        try
        {
            for (var i = 0; i < NativeCacheObservation.MaximumInstances; i++)
            {
                var created = NativeCacheObservation.TryCreate(owner with { InstanceId = "bounded-" + i }, 16,
                    () => owner.RuntimeEpoch, out _);
                if (created is not null) observedInstances.Add(created);
            }
            Check(observedInstances.Count == 64, "fixed observed-instance capacity");
            var overflow = NativeCacheObservation.TryCreate(owner, 1, () => owner.RuntimeEpoch, out var reason);
            Check(overflow is null && reason == "observed-instance-cap", "capacity loss is explicitly unavailable");
            overflow?.Dispose();
        }
        finally { foreach (var item in observedInstances) item.Dispose(); }
        var replacement = NativeCacheObservation.TryCreate(owner, 1, () => owner.RuntimeEpoch, out _);
        Check(replacement is not null, "instance stop releases diagnostic capacity");
        replacement?.Dispose();
        Check(NativeCacheObservation.Boundaries.Length + 1 <= 9 &&
            NativeCacheObservation.MaximumTokens == 1048576, "retained digest/token bounds");
        return count;
    }

    private static void PoolChecks(NativeCacheOwner owner, Action<bool, string> check)
    {
        var pool = new LlamaSlotPool(1);
        try
        {
            pool.ConfigureNativeCache(owner, () => owner.RuntimeEpoch);
            var rows = new List<NativeCacheGenerationObservation>(); var releases = 0;
            var input = JsonSerializer.SerializeToElement(Enumerable.Range(0, 137).ToArray());
            var metadata = new PreparedModelMetadata(owner.InstanceId, "llama.cpp.native.text", "1.1.0",
                owner.ModelId, 137, 32768, 1024, LlamaProtocol.CountBasis);
            NativeCacheBinding Binding(LlamaSlotPool.Lease lease) =>
                new("a:" + lease.LeaseId, "a", lease.LeaseId, "attempt", owner.ModelId, owner.InstanceId,
                    lease.LeaseId, lease.Id, "task-generation", "local-host-invocation");
            LlamaSlotPool.Lease Acquire(bool compatible = true)
            {
                var lease = pool.AcquireAsync(() => releases++, CancellationToken.None,
                    nativeCacheCompatible: compatible).GetAwaiter().GetResult();
                lease.BindNativeCache(Binding(lease), rows.Add); return lease;
            }
            void Generate(LlamaSlotPool.Lease lease)
            {
                var attempt = lease.BeginNativeCache(input, metadata, true, CancellationToken.None);
                lease.CompleteNativeCache(attempt, new(ModelGenerationFinish.Stop, ""), true, true, true, true,
                    false, NativeCacheProviderEvidence.Missing);
            }
            var first = Acquire(); Generate(first); first.Dispose();
            check(rows.Count == 1 && rows[0].ActualGenerateSequence == 1, "pool actual Generate captured");
            var neverGenerated = Acquire(); neverGenerated.Dispose();
            var next = Acquire(); Generate(next); next.Dispose();
            check(rows[^1].ActualGenerateSequence == 2 && rows[^1].Comparison.SamePreparedInput is null,
                "noGenerate return invalidates before allocator return without advancing");
            var unsupported = Acquire(compatible: false); unsupported.Dispose();
            var afterUnsupported = Acquire(); Generate(afterUnsupported); afterUnsupported.Dispose();
            check(rows[^1].Comparison.SamePreparedInput is null, "unsupported lease entry cannot leave stale comparable input");
            var rebound = Acquire();
            rebound.BindNativeCache(Binding(rebound) with { AttemptId = "", InstanceId = "wrong" }, rows.Add);
            Generate(rebound); rebound.Dispose();
            var afterBadBinding = Acquire(); Generate(afterBadBinding); afterBadBinding.Dispose();
            check(rows[^1].Comparison.SamePreparedInput is null, "failed rebinding cannot promote previous valid entry");
            var quarantined = Acquire();
            quarantined.Quarantine("synthetic");
            quarantined.Dispose();
            check(pool.Quarantined.Count == 1 && pool.AvailableCount == 0, "optional diagnostics preserve quarantine");
            check(releases == 7, "ordinary lease returns preserved, quarantined lease not returned");
            quarantined.InvalidateNativeCache("arbitrary-provider-private-text");
            check(NativeCacheObservation.ReasonCode("arbitrary-provider-private-text") == "capture-unavailable",
                "private reason inputs normalize to finite labels");
        }
        finally { pool.Complete(); }
        check(pool.NativeCacheOwner?.ObservationState == "unavailable", "stopped pool observation cannot revive");
    }
}
