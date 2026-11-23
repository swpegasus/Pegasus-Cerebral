using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Models;

namespace Cerebral.Worker;

// Private development-only records. Digests, token arrays and the hashing key never enter these DTOs.
internal sealed record NativeExecutableIdentity(string State, string Scope, string? ExecutableSha256,
    long? ExecutableBytes, int? NativeProcessId, long? NativeStartTicks, string Basis,
    string DependencyIdentityState, string? Reason)
{
    internal static NativeExecutableIdentity Unobserved(string reason) =>
        new("unobserved", "main-executable", null, null, null, null, "unobserved", "unobserved", reason);
}
internal sealed record NativeCacheOwner(string ModelId, string InstanceId, string RuntimeGenerationId,
    long RuntimeEpoch, string? LaunchBindingSha256, NativeExecutableIdentity Identity)
{
    public string ObservationState { get; init; } = "observed";
    public string? ObservationReason { get; init; }
}
internal sealed record NativeCacheBinding(string RowId, string AccountingGenerationId, string AccountingEntryId,
    string AttemptId, string ModelId, string InstanceId, string LeaseId, int NativeSlotId,
    string Purpose, string ExecutionAttribution);
internal sealed record NativePrefixComparison(string State, string? Reason, string? BaselineRowId,
    string? BaselineLeaseId, long? BaselineGenerateSequence, int? PreviousInputTokens, int? CurrentInputTokens,
    bool? SamePreparedInput, bool? PreviousInputIsPrefixOfCurrent,
    int? CommonPrefixLowerBoundTokens, int? CommonPrefixUpperBoundTokens)
{
    internal static NativePrefixComparison Unavailable(string reason, int? input = null) =>
        new("unavailable", reason, null, null, null, null, input, null, null, null, null);
    internal static NativePrefixComparison NoBaseline(string reason, int input) =>
        new("no-baseline", reason, null, null, null, null, input, null, null, null, null);
}
internal sealed record NativeCacheProviderEvidence(string? CachedSource, string? ProcessedSource,
    int? NativeTaskId, string TaskJoin, string RetentionState, string? Event, string? ReasonCode, string? Basis)
{
    internal static NativeCacheProviderEvidence Missing { get; } =
        new(null, null, null, "unobserved", "unobserved", null, null, null);
}
internal sealed record NativeCacheGenerationObservation(NativeCacheOwner Owner, NativeCacheBinding? Binding,
    string LeaseId, int NativeSlotId, long ActualGenerateSequence, NativePrefixComparison Comparison,
    string DispatchState, bool NativeStopConfirmed, NativeCacheProviderEvidence ProviderEvidence,
    DateTimeOffset ObservedAt);

// Independent, content-free native decision evidence. Never used as an admission/stop receipt.
internal sealed record NativeCacheDecisionPacket(string Schema, string Kind, int NativeSlotId, int NativeTaskId,
    string SourceClock, string SourceTimeUs, bool CachePrompt, int InputTokens, int RetainedPromptTokensBefore,
    int CachedTokensAtAdmission, string CheckpointOutcome, string ComparisonOutcome, bool ExactInputLogitAdjustment,
    int? NativeCommonPrefixTokens, int? NPastBeforeCheckpoint, int? NPastAfterCheckpoint,
    int? CheckpointPosMin, int? CheckpointPosMax, int? CheckpointTokens, int? CheckpointLimit,
    int? CheckpointMinStepTokens, bool? CheckpointGateEntered, bool? AloraClamped, bool? ChunkReuseApplied);
internal sealed record NativeCacheDecisionObservation(NativeCacheOwner Owner, NativeCacheBinding Binding,
    string LeaseId, int NativeSlotId, long ActualGenerateSequence, int InputTokens, string Kind, string State,
    NativeCacheDecisionPacket? Packet, int? IndependentTaskId, bool TaskConflict, DateTimeOffset IngestedAt);

internal static class NativeCacheDecisionCapture
{
    internal const string Field = "pegasus_native_cache_decision";
    internal const string Schema = "pegasus.native-cache-decision/1";
    private static readonly string[] Fields = ["schema", "kind", "nativeSlotId", "nativeTaskId", "sourceClock", "sourceTimeUs",
        "cachePrompt", "inputTokens", "retainedPromptTokensBefore", "cachedTokensAtAdmission", "checkpointOutcome",
        "comparisonOutcome", "exactInputLogitAdjustment", "nativeCommonPrefixTokens", "nPastBeforeCheckpoint",
        "nPastAfterCheckpoint", "checkpointPosMin", "checkpointPosMax", "checkpointTokens", "checkpointLimit",
        "checkpointMinStepTokens", "checkpointGateEntered", "aloraClamped", "chunkReuseApplied"];

    internal static void Requested(NativeCacheGeneration? attempt)
    {
        if (attempt is not { Eligible: true, Binding: not null, DecisionPublish: not null } ||
            Interlocked.Exchange(ref attempt.DecisionRequested, 1) != 0) return;
        Publish(attempt, "requested", "native-event-not-received");
    }
    internal static void Frame(NativeCacheGeneration? attempt, JsonElement root)
    {
        if (attempt is null || Volatile.Read(ref attempt.DecisionRequested) == 0) return;
        try
        {
            var occurrences = 0; JsonElement value = default;
            foreach (var property in root.EnumerateObject())
                if (property.NameEquals(Field)) { occurrences++; value = property.Value; }
            if (occurrences == 0) return; // Old native omission is unavailable, never a computed zero.
            if (occurrences != 1) { Publish(attempt, "event", "malformed-native-event"); return; }
            var packet = Parse(value, out var reason);
            if (packet is not null && (packet.NativeSlotId != attempt.Slot ||
                packet.InputTokens != attempt.Comparison.CurrentInputTokens))
            { packet = null; reason = "native-binding-mismatch"; }
            Publish(attempt, "event", packet is null ? reason : "observed", packet);
        }
        catch (Exception error) when (NativeCacheObservation.Ordinary(error))
        { Publish(attempt, "event", "optional-capture-failed"); }
    }
    internal static void Task(NativeCacheGeneration? attempt, int? task, bool conflict)
    {
        if (attempt is null || Volatile.Read(ref attempt.DecisionRequested) == 0 || task is null && !conflict) return;
        Publish(attempt, "task", "task-observation", task: task, conflict: conflict);
    }
    private static void Publish(NativeCacheGeneration attempt, string kind, string state,
        NativeCacheDecisionPacket? packet = null, int? task = null, bool conflict = false)
    {
        try
        {
            if (attempt.Binding is not { } binding || attempt.Comparison.CurrentInputTokens is not { } input) return;
            attempt.DecisionPublish?.Invoke(new(attempt.Owner, binding, attempt.Lease, attempt.Slot,
                attempt.Sequence, input, kind, state, packet, task, conflict, DateTimeOffset.UtcNow));
        }
        catch (Exception error) when (NativeCacheObservation.Ordinary(error)) { }
    }
    internal static NativeCacheDecisionPacket? Parse(JsonElement value, out string reason)
    {
        reason = "malformed-native-event";
        if (value.ValueKind != JsonValueKind.Object) return null;
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (fields.Count >= 32 || !Fields.Contains(property.Name, StringComparer.Ordinal) ||
                !fields.TryAdd(property.Name, property.Value) ||
                property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return null;
        }
        if (fields.Count != Fields.Length) return null;
        // Only the bounded scalar object is transiently inspected; neither it nor the enclosing frame is retained.
        var raw = value.GetRawText();
        if (raw.Length > 4096 || Encoding.UTF8.GetByteCount(raw) > 4096) return null;
        string Text(string name) => fields[name].ValueKind == JsonValueKind.String &&
            fields[name].GetString() is { Length: <= 48 } text ? text : throw new FormatException();
        int Integer(string name, int minimum = 0, int maximum = NativeCacheObservation.MaximumTokens) =>
            fields[name].ValueKind == JsonValueKind.Number && fields[name].TryGetInt32(out var n) && n >= minimum && n <= maximum
                ? n : throw new FormatException();
        int? Count(string name) => fields[name].ValueKind == JsonValueKind.Null ? null : Integer(name);
        bool Boolean(string name) => fields[name].ValueKind is JsonValueKind.True or JsonValueKind.False
            ? fields[name].GetBoolean() : throw new FormatException();
        bool? Flag(string name) => fields[name].ValueKind == JsonValueKind.Null ? null : Boolean(name);
        try
        {
            var schema = Text("schema");
            if (schema != Schema) { reason = "unsupported-event-schema"; return null; }
            var kind = Text("kind"); var clock = Text("sourceClock"); var time = Text("sourceTimeUs");
            var compared = Text("comparisonOutcome"); var checkpoint = Text("checkpointOutcome");
            if (kind != "prefix-checkpoint-decision" || clock != "ggml_time_us" || time.Length is < 1 or > 20 ||
                time.Length > 1 && time[0] == '0' || time.Any(c => c is < '0' or > '9') ||
                !long.TryParse(time, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _) ||
                compared is not ("computed" or "cache-disabled" or "unsupported-route") ||
                checkpoint is not ("not-entered" or "not-required" or "restored" or "reset-no-usable-checkpoint" or "unavailable")) return null;
            var packet = new NativeCacheDecisionPacket(schema, kind, Integer("nativeSlotId", 0, int.MaxValue),
                Integer("nativeTaskId", 0, int.MaxValue), clock, time, Boolean("cachePrompt"), Integer("inputTokens"),
                Integer("retainedPromptTokensBefore"), Integer("cachedTokensAtAdmission"), checkpoint, compared,
                Boolean("exactInputLogitAdjustment"), Count("nativeCommonPrefixTokens"), Count("nPastBeforeCheckpoint"),
                Count("nPastAfterCheckpoint"), Count("checkpointPosMin"), Count("checkpointPosMax"), Count("checkpointTokens"),
                fields["checkpointLimit"].ValueKind == JsonValueKind.Null ? null : Integer("checkpointLimit", int.MinValue, int.MaxValue),
                Count("checkpointMinStepTokens"), Flag("checkpointGateEntered"), Flag("aloraClamped"), Flag("chunkReuseApplied"));
            if (!Consistent(packet)) return null;
            reason = "observed"; return packet;
        }
        catch (Exception error) when (NativeCacheObservation.Ordinary(error)) { return null; }
    }
    private static bool Consistent(NativeCacheDecisionPacket p)
    {
        if (p.InputTokens < 1 || p.CachedTokensAtAdmission > p.InputTokens ||
            p.NativeCommonPrefixTokens > Math.Min(p.InputTokens, p.RetainedPromptTokensBefore) ||
            p.NPastBeforeCheckpoint > p.InputTokens || p.NPastAfterCheckpoint > p.InputTokens) return false;
        if (p.ComparisonOutcome == "unsupported-route")
            return p.NativeCommonPrefixTokens is null && p.NPastBeforeCheckpoint is null && p.NPastAfterCheckpoint is null &&
                p.CheckpointOutcome == "unavailable" && p.CheckpointGateEntered is null && p.AloraClamped is null &&
                p.ChunkReuseApplied is null && p.CheckpointPosMin is null && p.CheckpointPosMax is null &&
                p.CheckpointTokens is null && !p.ExactInputLogitAdjustment && p.CachedTokensAtAdmission == 0;
        if (p.NPastBeforeCheckpoint is null || p.NPastAfterCheckpoint is not { } after || p.CheckpointGateEntered is null) return false;
        if (p.ComparisonOutcome == "computed" && (!p.CachePrompt || p.NativeCommonPrefixTokens is null ||
            p.AloraClamped is null || p.ChunkReuseApplied is null)) return false;
        if (p.ComparisonOutcome == "cache-disabled" && (p.CachePrompt || p.NativeCommonPrefixTokens is not null ||
            p.AloraClamped is not null || p.ChunkReuseApplied is not null || p.NPastBeforeCheckpoint != 0 ||
            after != 0 || p.CachedTokensAtAdmission != 0 || p.CheckpointGateEntered != false ||
            p.CheckpointOutcome != "not-entered" || p.ExactInputLogitAdjustment)) return false;
        // These are the only text-prefix adjustments before the checkpoint search in the pinned native producer.
        if (p.ComparisonOutcome == "computed" &&
            (p.AloraClamped == false && p.ChunkReuseApplied == false && p.NPastBeforeCheckpoint != p.NativeCommonPrefixTokens ||
             p.AloraClamped == true && p.ChunkReuseApplied == false && p.NPastBeforeCheckpoint >= p.NativeCommonPrefixTokens ||
             p.AloraClamped == false && p.ChunkReuseApplied == true && p.NPastBeforeCheckpoint <= p.NativeCommonPrefixTokens)) return false;
        if (after > p.NPastBeforeCheckpoint) return false;
        // Native checkpoint search requires a positive candidate; complete input always reevaluates one token.
        if (p.NPastBeforeCheckpoint == 0 && p.CheckpointGateEntered == true) return false;
        if (p.CachedTokensAtAdmission != after - (p.ExactInputLogitAdjustment ? 1 : 0) ||
            p.ExactInputLogitAdjustment != (after > 0 && after == p.InputTokens)) return false;
        if (p.CheckpointOutcome == "restored")
            return p.CheckpointGateEntered == true &&
                (p.CheckpointPosMin is null || p.CheckpointPosMax is null || p.CheckpointPosMin <= p.CheckpointPosMax) &&
                (p.CheckpointTokens is null || after <= p.CheckpointTokens);
        if (p.CheckpointPosMin is not null || p.CheckpointPosMax is not null || p.CheckpointTokens is not null) return false;
        return p.CheckpointOutcome == "reset-no-usable-checkpoint"
            ? p.CheckpointGateEntered == true && after == 0
            : p.CheckpointOutcome is "not-entered" or "not-required" && p.CheckpointGateEntered == false &&
                p.NPastBeforeCheckpoint == p.NPastAfterCheckpoint;
    }
}

internal static class NativeCacheDevelopmentSetup
{
    internal static bool Capture(ModelExecutionAccounting accounting)
    {
        var enabled = false;
        try
        {
            enabled = Environment.GetEnvironmentVariable("CEREBRAL_DEV_NATIVE_CACHE_OBSERVATION") == "1";
            accounting.Observation.NativeCacheConfigured(enabled);
        }
        catch (Exception e) when (NativeCacheObservation.Ordinary(e))
        {
            try { accounting.Observation.NativeCacheCaptureFailed(); }
            catch (Exception nested) when (NativeCacheObservation.Ordinary(nested)) { }
        }
        return enabled;
    }
}

internal sealed class NativeCacheGeneration
{
    internal required NativeCacheOwner Owner;
    internal required int Slot;
    internal required string Lease;
    internal required long Sequence;
    internal required long Version;
    internal required NativePrefixComparison Comparison;
    internal NativeCacheBinding? Binding;
    internal Action<NativeCacheGenerationObservation>? Publish;
    internal Action<NativeCacheDecisionObservation>? DecisionPublish;
    internal int DecisionRequested;
    internal NativeCacheObservation.Digests? Input;
    internal bool Eligible;
    internal int Finished;
}

// This is prepared-INPUT history, not resident-KV state. In particular, generated suffixes are unmeasured.
internal sealed class NativeCacheObservation : IDisposable
{
    internal const int MaximumInstances = 64;
    internal const int MaximumTokens = 1_048_576;
    internal static readonly int[] Boundaries = [64, 256, 1024, 4096, 16384, 65536, 262144, 1048576];
    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("Cerebral.NativePreparedPrefix/1\0");
    private static int activeInstances;
    private readonly object gate = new();
    private readonly Slot[] slots;
    private readonly Func<long> currentEpoch;
    private readonly Func<DateTimeOffset> now;
    private readonly byte[] key;
    private readonly bool counted;
    private NativeCacheOwner owner;
    private bool disposed, coverageLost;

    private sealed class Slot
    {
        internal long Sequence, Version;
        internal Baseline? Previous;
        internal string Reason = "no-baseline";
    }
    internal sealed class Digests(int count, Dictionary<int, byte[]> values) : IDisposable
    {
        internal int Count { get; } = count;
        internal Dictionary<int, byte[]> Values { get; } = values;
        public void Dispose() { foreach (var value in Values.Values) CryptographicOperations.ZeroMemory(value); Values.Clear(); }
    }
    private sealed record Baseline(NativeCacheBinding Binding, long Sequence, Digests Input, DateTimeOffset At);

    internal NativeCacheObservation(NativeCacheOwner owner, int capacity, Func<long> currentEpoch,
        Func<DateTimeOffset>? now = null, byte[]? testKey = null, bool counted = false)
    {
        if (capacity is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.owner = owner; this.currentEpoch = currentEpoch;
        this.now = now ?? (() => DateTimeOffset.UtcNow); this.counted = counted;
        key = testKey is null ? RandomNumberGenerator.GetBytes(32) : (byte[])testKey.Clone();
        if (key.Length != 32) throw new ArgumentException("Diagnostic key size.");
        slots = Enumerable.Range(0, capacity).Select(_ => new Slot()).ToArray();
    }

    internal static NativeCacheObservation? TryCreate(NativeCacheOwner owner, int capacity,
        Func<long> currentEpoch, out string? reason)
    {
        reason = null;
        if (Interlocked.Increment(ref activeInstances) > MaximumInstances)
        { Interlocked.Decrement(ref activeInstances); reason = "observed-instance-cap"; return null; }
        try { return new(owner, capacity, currentEpoch, counted: true); }
        catch (Exception e) when (Ordinary(e))
        { Interlocked.Decrement(ref activeInstances); reason = "observation-initialization-unavailable"; return null; }
    }
    internal NativeCacheOwner Owner
    {
        get
        {
            var epoch = ReadEpoch();
            lock (gate)
            {
                Refresh(epoch);
                return owner with { ObservationState = disposed || coverageLost ? "unavailable" : "observed",
                    ObservationReason = disposed ? "instance-stopped" : coverageLost ? "epoch-unavailable" : null };
            }
        }
    }
    private long? ReadEpoch()
    {
        try { return currentEpoch(); }
        catch (Exception e) when (Ordinary(e)) { return null; }
    }
    // currentEpoch is an in-memory read only. It is called outside this lock.
    private void Refresh(long? epoch)
    {
        if (epoch is null) { coverageLost = true; InvalidateAll("epoch-unavailable"); return; }
        if (epoch.Value == owner.RuntimeEpoch) return;
        if (epoch.Value < owner.RuntimeEpoch)
        { coverageLost = true; InvalidateAll("runtime-epoch-regressed"); return; }
        owner = owner with { RuntimeEpoch = epoch.Value };
        InvalidateAll("runtime-epoch-changed");
    }
    private void InvalidateAll(string reason)
    { foreach (var slot in slots) Invalidate(slot, reason); }
    private static void Invalidate(Slot slot, string reason)
    {
        slot.Version++; slot.Previous?.Input.Dispose(); slot.Previous = null; slot.Reason = reason;
    }
    internal void Invalidate(int slot, string reason)
    {
        reason = ReasonCode(reason);
        lock (gate)
        {
            if ((uint)slot >= slots.Length) { coverageLost = true; InvalidateAll("slot-outside-observation"); return; }
            Invalidate(slots[slot], reason);
        }
    }
    internal NativeCacheGeneration Begin(int slotId, string lease, NativeCacheBinding? binding,
        Action<NativeCacheGenerationObservation>? publish, JsonElement prompt, int inputTokens,
        bool supportedText, CancellationToken token, Action<NativeCacheDecisionObservation>? decisionPublish = null)
    {
        var epoch = ReadEpoch(); var at = now();
        NativeCacheGeneration attempt; Baseline? previous; byte[] localKey;
        lock (gate)
        {
            Refresh(epoch);
            var slot = slots[slotId];
            previous = slot.Previous; slot.Previous = null;
            var reason = slot.Reason;
            if (previous is not null && at - previous.At >= TimeSpan.FromMinutes(5))
            { previous.Input.Dispose(); previous = null; reason = "baseline-expired"; }
            slot.Sequence++; slot.Version++; slot.Reason = "prior-native-finalization-unavailable";
            var bound = ValidBinding(binding, owner, slotId, lease) && publish is not null;
            attempt = new() { Owner = owner, Slot = slotId, Lease = lease, Sequence = slot.Sequence,
                Version = slot.Version, Binding = binding, Publish = publish, DecisionPublish = decisionPublish,
                Eligible = supportedText && bound && !disposed && !coverageLost,
                Comparison = reason is "no-baseline" or "baseline-expired"
                    ? NativePrefixComparison.NoBaseline(reason, inputTokens)
                    : NativePrefixComparison.Unavailable(reason, inputTokens) };
            localKey = (byte[])key.Clone();
        }
        try
        {
            if (!attempt.Eligible)
            {
                attempt.Comparison = NativePrefixComparison.Unavailable(!supportedText ? "unsupported-native-use" :
                    !ValidBinding(binding, attempt.Owner, slotId, lease) || publish is null
                        ? "exact-entry-binding-unavailable" : "observation-coverage-unavailable", inputTokens);
                return attempt;
            }
            attempt.Input = Hash(prompt, inputTokens, localKey, token);
            if (previous is not null)
                attempt.Comparison = Compare(previous, attempt.Input, prompt, localKey, token);
            lock (gate)
            {
                if (disposed || coverageLost || slots[slotId].Version != attempt.Version ||
                    owner.RuntimeEpoch != attempt.Owner.RuntimeEpoch)
                { attempt.Eligible = false; attempt.Comparison = NativePrefixComparison.Unavailable("source-changed", inputTokens); }
            }
        }
        catch (Exception e) when (Ordinary(e))
        {
            attempt.Eligible = false; attempt.Input?.Dispose(); attempt.Input = null;
            attempt.Comparison = NativePrefixComparison.Unavailable("prefix-observation-unavailable", inputTokens);
        }
        finally { previous?.Input.Dispose(); CryptographicOperations.ZeroMemory(localKey); }
        return attempt;
    }

    internal void Complete(NativeCacheGeneration attempt, ModelGenerationResult result, bool sent,
        bool acknowledged, bool confirmedIdle, bool live, bool cancelled, NativeCacheProviderEvidence evidence)
    {
        if (Interlocked.Exchange(ref attempt.Finished, 1) != 0) return;
        var epoch = ReadEpoch(); var at = now();
        NativeCacheGenerationObservation observation;
        lock (gate)
        {
            Refresh(epoch);
            var slot = slots[attempt.Slot];
            var current = !disposed && !coverageLost && slot.Version == attempt.Version &&
                owner.RuntimeEpoch == attempt.Owner.RuntimeEpoch;
            var nativeSettled = sent && acknowledged && confirmedIdle && live && !cancelled && result.Error is null &&
                result.Finish is ModelGenerationFinish.Stop or ModelGenerationFinish.Length;
            var success = current && attempt.Eligible && attempt.Input is not null && nativeSettled;
            if (current)
            {
                if (success)
                {
                    slot.Previous = new(attempt.Binding!, attempt.Sequence, attempt.Input!, at);
                    attempt.Input = null; slot.Reason = "settled-native-input";
                }
                else Invalidate(slot, "prior-native-generation-ineligible");
            }
            var comparison = current ? attempt.Comparison :
                NativePrefixComparison.Unavailable("source-changed", attempt.Comparison.CurrentInputTokens);
            var observedOwner = current ? attempt.Owner : attempt.Owner with
                { ObservationState = "unavailable", ObservationReason = disposed ? "instance-stopped" : "source-changed" };
            observation = new(observedOwner, attempt.Binding, attempt.Lease, attempt.Slot, attempt.Sequence,
                comparison, nativeSettled ? "settled" : acknowledged ? "acknowledged" : sent ? "uncertain" : "send-intent",
                current && confirmedIdle && live, evidence, at);
        }
        attempt.Input?.Dispose(); attempt.Input = null;
        // Never invoke the registry sink while holding a pool diagnostic lock.
        try { attempt.Publish?.Invoke(observation); }
        catch (Exception e) when (Ordinary(e)) { Invalidate(attempt.Slot, "diagnostic-publication-unavailable"); }
    }

    private static bool ValidBinding(NativeCacheBinding? b, NativeCacheOwner owner, int slot, string lease) =>
        b is not null && b.ModelId == owner.ModelId && b.InstanceId == owner.InstanceId &&
        b.NativeSlotId == slot && b.LeaseId == lease && b.Purpose == "task-generation" &&
        b.ExecutionAttribution == "local-host-invocation" && Identifier(b.AccountingGenerationId) &&
        Identifier(b.AccountingEntryId) && Identifier(b.AttemptId) &&
        b.RowId == b.AccountingGenerationId + ":" + b.AccountingEntryId;
    private static bool Identifier(string? value) => value is { Length: > 0 and <= 256 } && !value.Contains('\0');

    private static Digests Hash(JsonElement prompt, int count, byte[] key, CancellationToken token)
    {
        if (prompt.ValueKind != JsonValueKind.Array || count is < 1 or > MaximumTokens || prompt.GetArrayLength() != count)
            throw new InvalidDataException("Unsupported diagnostic token shape.");
        var values = new Dictionary<int, byte[]>();
        try
        {
            var clock = Stopwatch.StartNew();
            foreach (var boundary in Boundaries.Where(x => x <= count).Append(count).Distinct())
                values.Add(boundary, HashPrefix(prompt, boundary, key, token, clock));
            return new(count, values);
        }
        catch { foreach (var value in values.Values) CryptographicOperations.ZeroMemory(value); throw; }
    }
    private static byte[] HashPrefix(JsonElement prompt, int boundary, byte[] key,
        CancellationToken token, Stopwatch clock)
    {
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hash.AppendData(Domain);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, boundary); hash.AppendData(length);
        Span<byte> buffer = stackalloc byte[4096];
        var used = 0; var read = 0;
        foreach (var item in prompt.EnumerateArray())
        {
            if (!item.TryGetInt32(out var id) || id < 0) throw new InvalidDataException("Invalid diagnostic token.");
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(used, 4), id); used += 4; read++;
            if (used == buffer.Length || read == boundary)
            {
                token.ThrowIfCancellationRequested();
                if (clock.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("Diagnostic hash budget.");
                hash.AppendData(buffer[..used]); used = 0;
            }
            if (read == boundary) return hash.GetHashAndReset();
        }
        throw new InvalidDataException("Incomplete diagnostic token prefix.");
    }
    private static NativePrefixComparison Compare(Baseline previous, Digests current, JsonElement prompt,
        byte[] key, CancellationToken token)
    {
        var prior = previous.Input; var minimum = Math.Min(prior.Count, current.Count);
        var lower = 0; var upper = minimum;
        foreach (var boundary in Boundaries.Where(x => x <= minimum))
        {
            if (!CryptographicOperations.FixedTimeEquals(prior.Values[boundary], current.Values[boundary]))
            { upper = boundary - 1; break; }
            lower = boundary;
        }
        var same = false; var previousIsPrefix = false;
        if (current.Count >= prior.Count)
        {
            byte[]? temporary = null;
            try
            {
                var digest = current.Values.TryGetValue(prior.Count, out var known) ? known :
                    temporary = HashPrefix(prompt, prior.Count, key, token, Stopwatch.StartNew());
                previousIsPrefix = CryptographicOperations.FixedTimeEquals(prior.Values[prior.Count], digest);
                same = prior.Count == current.Count && previousIsPrefix;
                if (previousIsPrefix) lower = upper = prior.Count;
                else upper = Math.Min(upper, prior.Count - 1);
            }
            finally { if (temporary is not null) CryptographicOperations.ZeroMemory(temporary); }
        }
        else if (prior.Values.TryGetValue(current.Count, out var priorAtCurrent))
        {
            if (CryptographicOperations.FixedTimeEquals(priorAtCurrent, current.Values[current.Count]))
                lower = upper = current.Count;
            else upper = Math.Min(upper, current.Count - 1);
        }
        return new("observed", null, previous.Binding.RowId, previous.Binding.LeaseId, previous.Sequence,
            prior.Count, current.Count, same, previousIsPrefix, lower, upper);
    }

    internal static NativeCacheProviderEvidence Evidence(JsonElement root, PromptCacheMetrics? metrics,
        int? taskId, bool taskConflict)
    {
        try
        {
            string? cached = null, processed = null;
            var cachedBad = metrics?.Diagnostic is not null;
            var processedBad = cachedBad;
            if (root.TryGetProperty("timings", out var timing))
            {
                if (timing.ValueKind != JsonValueKind.Object) cachedBad = processedBad = true;
                else
                {
                    if (timing.TryGetProperty("cache_n", out var c))
                    {
                        if (CurrentCount(c, metrics?.CachedTokens)) cached = "timings.cache_n";
                        else cachedBad = true;
                    }
                    if (timing.TryGetProperty("prompt_n", out var p))
                    {
                        if (CurrentCount(p, metrics?.ProcessedPromptTokens)) processed = "timings.prompt_n";
                        else processedBad = true;
                    }
                }
            }
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null)
            {
                if (usage.ValueKind != JsonValueKind.Object) cachedBad = true;
                else if (usage.TryGetProperty("prompt_tokens_details", out var details))
                {
                    if (details.ValueKind != JsonValueKind.Object) cachedBad = true;
                    else if (details.TryGetProperty("cached_tokens", out var c))
                    {
                        if (!CurrentCount(c, metrics?.CachedTokens)) cachedBad = true;
                        else cached ??= "usage.prompt_tokens_details.cached_tokens";
                    }
                }
            }
            return new(cachedBad ? null : cached, processedBad ? null : processed, taskConflict ? null : taskId,
                taskConflict ? "conflicting-native-task-id" : taskId.HasValue ? "existing-ack-slot-status" : "unobserved",
                "unobserved", null, null, null);
        }
        catch (Exception e) when (Ordinary(e)) { return NativeCacheProviderEvidence.Missing; }
    }
    private static bool CurrentCount(JsonElement value, int? accepted) =>
        accepted is { } expected && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var current) && current >= 0 && current == expected;
    internal static string ReasonCode(string reason) => reason switch
    {
        "unsupported-lease-entry" or "unsupported-reasoning-route" or "exact-entry-binding-unavailable" or
        "capture-unavailable" or "native-slot-quarantined" or "lease-return-without-native-finalization" or
        "prior-native-finalization-unavailable" or "known-native-eviction" or "diagnostic-publication-unavailable" => reason,
        _ => "capture-unavailable"
    };
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; InvalidateAll("instance-stopped"); CryptographicOperations.ZeroMemory(key);
        }
        if (counted) Interlocked.Decrement(ref activeInstances);
    }
    internal static bool Ordinary(Exception e) => e is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}

internal readonly record struct NativeExecutableProcessSnapshot(int ProcessId, long StartTicks, bool Exited, string? Image = null);

internal sealed class NativeExecutableCapture : IDisposable
{
    private readonly FileStream? stream;
    private readonly string path;
    private readonly NativeExecutableIdentity file;
    private NativeExecutableCapture(string path, FileStream? stream, NativeExecutableIdentity file)
    { this.path = path; this.stream = stream; this.file = file; }
    internal static NativeExecutableCapture Open(string executable)
    {
        FileStream? stream = null;
        try
        {
            var path = Path.GetFullPath(executable);
            stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 256L * 1024 * 1024) throw new IOException("Diagnostic executable size.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; var clock = Stopwatch.StartNew(); int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("Diagnostic executable budget.");
                hash.AppendData(buffer, 0, read);
            }
            var identity = new NativeExecutableIdentity("launch-file-only", "main-executable",
                Convert.ToHexStringLower(hash.GetHashAndReset()), stream.Length, null, null,
                "read-handle-held-through-spawn", "unobserved", "owned-child-not-bound");
            return new(path, stream, identity);
        }
        catch (Exception e) when (NativeCacheObservation.Ordinary(e))
        {
            try { stream?.Dispose(); } catch (Exception nested) when (NativeCacheObservation.Ordinary(nested)) { }
            return new("", null, NativeExecutableIdentity.Unobserved("executable-file-observation-unavailable"));
        }
    }
    internal static NativeExecutableProcessSnapshot CaptureOwnedProcess(Process process)
    {
        try { return Observe(process, image: false); }
        catch (Exception e) when (NativeCacheObservation.Ordinary(e)) { return default; }
    }
    private static NativeExecutableProcessSnapshot Observe(Process process, bool image = true)
    {
        process.Refresh();
        var pid = process.Id; var ticks = process.StartTime.ToUniversalTime().Ticks; var exited = process.HasExited;
        var observedImage = image && !exited ? process.MainModule?.FileName : null;
        process.Refresh();
        return new(pid, ticks, exited || process.HasExited || process.Id != pid ||
            process.StartTime.ToUniversalTime().Ticks != ticks, observedImage);
    }
    internal Task<NativeExecutableIdentity> BindReadyAsync(Process process, NativeExecutableProcessSnapshot owned,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return stream is null ? Task.FromResult(file) : BindReadyAsync(file, path, owned,
            () => Observe(process), static cancellation => Task.Delay(25, cancellation), token);
    }
    // Only unavailable image metadata may be retried. A different image, child identity or exit
    // is final for this capture. The caller retains the file handle and startup deadline.
    internal static async Task<NativeExecutableIdentity> BindReadyAsync(NativeExecutableIdentity file, string expected,
        NativeExecutableProcessSnapshot owned, Func<NativeExecutableProcessSnapshot> observe,
        Func<CancellationToken, Task> delay, CancellationToken token)
    {
        const int maximumAttempts = 40;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            NativeExecutableIdentity result;
            try
            {
                var before = observe();
                token.ThrowIfCancellationRequested();
                var after = observe();
                result = Bind(file, expected, owned, before, after);
            }
            catch (Exception e) when (e is not OperationCanceledException && NativeCacheObservation.Ordinary(e))
            { token.ThrowIfCancellationRequested(); return file with { Reason = "owned-child-image-unobserved" }; }
            token.ThrowIfCancellationRequested();
            if (result.Reason != "owned-child-image-unobserved" || attempt == maximumAttempts - 1) return result;
            await delay(token).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Native identity observation bound.");
    }
    internal static NativeExecutableIdentity Bind(NativeExecutableIdentity file, string expected, string? image,
        int pid, long ticks, bool exited) => Bind(file, expected, new(pid, ticks, false),
            new(pid, ticks, exited, image), new(pid, ticks, exited, image));
    internal static NativeExecutableIdentity Bind(NativeExecutableIdentity file, string expected,
        NativeExecutableProcessSnapshot owned, NativeExecutableProcessSnapshot before, NativeExecutableProcessSnapshot after)
    {
        if (!OperatingSystem.IsWindows()) return file with { Reason = "owned-image-byte-binding-unobserved-platform" };
        if (file.ExecutableSha256 is not { Length: 64 } hash || !hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            file.ExecutableBytes is not (> 0 and <= 268_435_456) || file.State != "launch-file-only" ||
            file.Scope != "main-executable" || file.Basis != "read-handle-held-through-spawn")
            return NativeExecutableIdentity.Unobserved("executable-file-observation-unavailable");
        if (owned.ProcessId <= 0 || owned.StartTicks <= 0 || owned.Exited || before.Exited || after.Exited ||
            before.ProcessId != owned.ProcessId || after.ProcessId != owned.ProcessId ||
            before.StartTicks != owned.StartTicks || after.StartTicks != owned.StartTicks)
            return file with { Reason = "owned-child-not-bound" };
        if ((!string.IsNullOrEmpty(before.Image) && !string.Equals(Path.GetFullPath(expected), Path.GetFullPath(before.Image), StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(after.Image) && !string.Equals(Path.GetFullPath(expected), Path.GetFullPath(after.Image), StringComparison.OrdinalIgnoreCase)))
            return file with { Reason = "owned-child-image-mismatch" };
        if (string.IsNullOrEmpty(before.Image) || string.IsNullOrEmpty(after.Image))
            return file with { Reason = "owned-child-image-unobserved" };
        return file with { State = "observed-main-executable", NativeProcessId = owned.ProcessId,
            NativeStartTicks = owned.StartTicks, Reason = null };
    }
    public void Dispose()
    {
        try { stream?.Dispose(); }
        catch (Exception e) when (NativeCacheObservation.Ordinary(e)) { }
    }
}
