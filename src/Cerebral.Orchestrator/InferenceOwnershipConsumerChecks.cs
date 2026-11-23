using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cerebral.Orchestrator;

/// <summary>
/// Deterministic JSON and polling-state fixtures. No window, connection, endpoint,
/// dispatcher timer, model, provider, process or file is created by these checks.
/// </summary>
public static class InferenceOwnershipConsumerChecks
{
    private const string Owner = "11111111111111111111111111111111";
    private const string Generation = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Accounting = "33333333333333333333333333333333";
    private const string Runtime = "44444444444444444444444444444444";
    private const string Instance = "55555555555555555555555555555555";
    private const string Scheduler = "66666666666666666666666666666666";
    private const string NextGeneration = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OtherOwner = "88888888888888888888888888888888";
    private const string Stamp = "2026-10-15T20:30:00Z";

    public static async Task<int> RunAsync()
    {
        var checks = CheckProjection() + await CheckPollingAsync();
        Console.WriteLine($"Inference ownership consumer: {checks} deterministic JSON/polling-state checks passed; no UI, RPC, model or provider.");
        return checks;
    }

    private static int CheckProjection()
    {
        var checks = 0;
        void Check(bool value, string reason)
        {
            if (!value) throw new InvalidOperationException(reason);
            checks++;
        }
        static JsonElement Element(JsonObject value) => JsonSerializer.SerializeToElement(value);
        static JsonObject Counts(JsonObject value) => (JsonObject)value["counts"]!;
        static JsonArray Rows(JsonObject value) => (JsonArray)value["rows"]!;
        static JsonObject Active(JsonObject value) => (JsonObject)Rows(value)[1]!;
        static JsonObject Changed(Action<JsonObject> edit)
        {
            var value = Fixture(); edit(value); return value;
        }
        static InferenceOwnershipSample Read(JsonObject value) => InferenceOwnershipProjection.ReadSample(Element(value), Owner);
        void Reject(JsonElement value, string reason, string owner = Owner)
        {
            try { _ = InferenceOwnershipProjection.ReadSample(value, owner); }
            catch (InvalidDataException error)
            {
                Check(!error.Message.Contains("fixture-private-body", StringComparison.Ordinal), reason);
                return;
            }
            Check(false, reason);
        }
        void RejectChange(Action<JsonObject> edit, string reason) => Reject(Element(Changed(edit)), reason);

        var fixture = Fixture();
        var original = fixture.ToJsonString();
        var observed = Read(fixture);
        Check(observed.ObserverGenerationId == Generation && observed.Revision == 10 &&
              observed.SampledAt == DateTimeOffset.Parse(Stamp, System.Globalization.CultureInfo.InvariantCulture),
            "A valid full sample retains exact observer generation, revision and sampled time.");
        Check(Read(Changed(value => value["observerGenerationId"] = Generation.ToUpperInvariant())).ObserverGenerationId == Generation,
            "A valid UUID spelling is canonicalized before it can establish a polling generation.");
        Check(observed.Presentation.Summary.StartsWith("1 observed occupied slot  ·  Slot 0 [", StringComparison.Ordinal) &&
              observed.Presentation.Details.Contains("Accepted local work: 1 known profile identities; 2 known executions.", StringComparison.Ordinal),
            "Distinct working profiles and accepted executions remain independent facts.");
        Check(observed.Presentation.Details.Contains("Returned current rows: 1; distinct physical lease IDs: 1", StringComparison.Ordinal) &&
              observed.Presentation.Details.Contains("confirmed-release history: 1 rows, 1 distinct physical lease IDs", StringComparison.Ordinal) &&
              observed.Presentation.Details.Contains("reusable native slot 0", StringComparison.Ordinal),
            "Reusing one native slot does not merge a released lease with its distinct current lease.");
        Check(observed.Presentation.Details.Contains("Accepted-work source: observed", StringComparison.Ordinal) &&
              observed.Presentation.Details.Contains("age 30 ms", StringComparison.Ordinal) &&
              observed.Presentation.Details.Contains("age 20 ms", StringComparison.Ordinal) &&
              observed.Presentation.Details.Contains("age 10 ms", StringComparison.Ordinal),
            "Accepted-work, allocator and progress observations keep their independent source ages.");
        Check(observed.Presentation.Details.Contains("remote totals, exact context totals, provider activity", StringComparison.Ordinal) &&
              observed.Presentation.Details.Contains("not workflow, build or task success", StringComparison.Ordinal) &&
              !observed.Presentation.Summary.Contains("available agents", StringComparison.OrdinalIgnoreCase),
            "The projection qualifies remote/context uncertainty and does not turn leases or release into available agents or task success.");
        var mappedVision = Read(Changed(value => Active(value)["kind"] = "ImageVerification"));
        Check(mappedVision.Presentation.Summary.Contains("Vision check", StringComparison.Ordinal) &&
              mappedVision.Presentation.Details.Contains("captured kind ImageVerification", StringComparison.Ordinal) &&
              !mappedVision.Presentation.Summary.Contains("image verification observations", StringComparison.Ordinal),
            "An image check with an exact physical lease is labeled on that slot.");
        var unmappedVision = Read(Changed(value =>
        {
            Active(value)["kind"] = "ImageVerification";
            Active(value)["leaseId"] = null;
            Active(value)["nativeSlotId"] = null;
        }));
        Check(unmappedVision.Presentation.Summary.Contains("image verification observations · slot unobserved", StringComparison.Ordinal) &&
              !unmappedVision.Presentation.Summary.Contains("Vision check", StringComparison.Ordinal),
            "An image check without a physical lease cannot claim an inference slot.");
        Check(fixture.ToJsonString() == original,
            "Projection does not mutate its JSON source or leave hidden presentation state.");
        InferenceOwnershipSample detached;
        using (var document = JsonDocument.Parse(original))
            detached = InferenceOwnershipProjection.ReadSample(document.RootElement, Owner);
        Check(detached == observed && detached.Presentation.Details.Length > 0,
            "The returned presentation remains usable after the source JsonDocument has been disposed.");

        var prose = Read(Changed(value => value["semantics"] = "fixture-private-body is not trusted UI policy"));
        Check(!prose.Presentation.Details.Contains("fixture-private-body", StringComparison.Ordinal),
            "Validated producer prose is not echoed as consumer policy or private context.");
        var partial = Read(Changed(value =>
        {
            value["state"] = "partial"; value["partial"] = true;
            value["reasons"] = new JsonArray("accepted-execution-unprojectable");
            Counts(value)["completeWorkingProfileIdentities"] = null; Counts(value)["completeExecutions"] = null;
            ((JsonObject)Counts(value)["source"]!)["state"] = "partial";
            ((JsonObject)Counts(value)["source"]!)["reason"] = "required-coverage-unavailable";
        }));
        Check(partial.Presentation.Summary.Contains("partial observation", StringComparison.Ordinal) &&
              partial.Presentation.Details.Contains("Accepted local work: 1 known profile identities; 2 known executions.", StringComparison.Ordinal) &&
              partial.Presentation.Details.Contains("Complete accepted-work totals are unknown", StringComparison.Ordinal),
            "Partial accepted-work counts remain labeled observations, not complete totals.");
        var partialZero = Read(Changed(value =>
        {
            value["state"] = "partial"; value["partial"] = true; value["reasons"] = new JsonArray("scheduler-unobserved");
            Counts(value)["observedKnownWorkingProfileIdentities"] = 0; Counts(value)["observedKnownExecutions"] = 0;
            Counts(value)["completeWorkingProfileIdentities"] = null; Counts(value)["completeExecutions"] = null;
            ((JsonObject)Counts(value)["source"]!)["state"] = "partial";
            ((JsonObject)Counts(value)["source"]!)["reason"] = "required-coverage-unavailable";
            value["rows"] = new JsonArray(); value["retainedCurrentRows"] = 0;
        }));
        var observedZero = Read(Changed(value =>
        {
            Counts(value)["observedKnownWorkingProfileIdentities"] = 0; Counts(value)["observedKnownExecutions"] = 0;
            Counts(value)["completeWorkingProfileIdentities"] = 0; Counts(value)["completeExecutions"] = 0;
            value["rows"] = new JsonArray(); value["retainedCurrentRows"] = 0;
        }));
        Check(partialZero.Presentation.Summary.Contains("Slot occupants unobserved", StringComparison.Ordinal) &&
              partialZero.Presentation.Summary.Contains("partial observation", StringComparison.Ordinal) &&
              observedZero.Presentation.Summary == "Slot occupants unobserved" &&
              partialZero.Presentation.Details.Contains("Complete accepted-work totals are unknown", StringComparison.Ordinal) &&
              observedZero.Presentation.Details.Contains("Complete within the captured local scheduler coverage", StringComparison.Ordinal),
            "A complete empty observation can report zero while an incomplete zero remains unknown in total.");
        var stale = Read(Changed(value => Active(value)["progressSource"] = Source("stale", Accounting, null, 2001)));
        Check(stale.Presentation.Details.Contains("Progress source: stale", StringComparison.Ordinal) &&
              stale.Presentation.Details.Contains("age 2001 ms", StringComparison.Ordinal) &&
              stale.Presentation.Details.Contains("Allocator source: observed", StringComparison.Ordinal),
            "Stale provider progress does not silently refresh itself or stale an independently observed allocator.");
        var withoutLease = Read(Changed(value =>
        {
            Active(value)["leaseId"] = null; Active(value)["nativeSlotId"] = null;
            Active(value)["accountingLeaseId"] = "inbound-accounting-lease"; Active(value)["reservationId"] = "reservation-only";
            Active(value)["phase"] = "unknown"; Active(value)["uncertaintyCode"] = "ownership-unresolved";
        }));
        Check(withoutLease.Presentation.Summary.Contains("Slot occupants unobserved", StringComparison.Ordinal) &&
              withoutLease.Presentation.Details.Contains("distinct physical lease IDs: 0", StringComparison.Ordinal) &&
              withoutLease.Presentation.Details.Contains("rows without a captured physical lease: 1", StringComparison.Ordinal) &&
              withoutLease.Presentation.Details.Contains("unknown/quarantined/unresolved rows: 1", StringComparison.Ordinal),
            "Accounting leases, reservations and unresolved work cannot fabricate a native physical lease.");
        var terminal = Read(Changed(value =>
        {
            var row = (JsonObject)Rows(value)[0]!;
            row["phase"] = "terminal"; row["release"] = "unobserved"; row["releaseProof"] = null; row["releasedAt"] = null;
            value["retainedCurrentRows"] = 2;
        }));
        Check(terminal.Presentation.Summary.Contains("Slot 0", StringComparison.Ordinal) &&
              terminal.Presentation.Details.Contains("Returned current rows: 2; distinct physical lease IDs: 2", StringComparison.Ordinal) &&
              terminal.Presentation.Details.Contains("confirmed-release history: 0 rows", StringComparison.Ordinal),
            "A successful inference terminal result does not itself retire retained lease ownership.");

        Reject(Element(fixture), "Another verified owner cannot adopt the sample.", OtherOwner);
        RejectChange(value => value["schema"] = "cerebral.inference-ownership/2", "An unknown sample schema is refused.");
        RejectChange(value => value["observerGenerationId"] = "not-a-generation", "Malformed observer generation is refused.");
        RejectChange(value => value["observerGenerationId"] = Guid.Empty.ToString("N"), "An empty observer UUID is refused.");
        RejectChange(value => value["revision"] = -1, "A negative sample revision is refused.");
        RejectChange(value => value["revision"] = 1.5, "A fractional sample revision is refused.");
        RejectChange(value => Counts(value)["observedKnownExecutions"] = "2", "A string is not a numeric accepted-execution observation.");
        RejectChange(value => Counts(value)["completeContextCount"] = 0, "Uncaptured exact context totals cannot become a known zero.");
        RejectChange(value => Counts(value)["observedKnownWorkingProfileIdentities"] = 3,
            "Distinct profile identities cannot exceed accepted execution contexts.");
        RejectChange(value => value["remoteState"] = "online", "A local observation cannot assert an unimplemented remote total.");
        RejectChange(value => value["executionAdmissionOpen"] = true, "The unavailable combined admission policy cannot be invented.");
        RejectChange(value => Active(value)["outputTokens"] = -1, "Negative provider token counts are refused.");
        RejectChange(value => Active(value)["nativeSlotId"] = -1, "Negative reusable slot coordinates are refused.");
        RejectChange(value => ((JsonObject)Active(value)["progressSource"]!)["ageMilliseconds"] = -1,
            "A negative source age cannot enter presentation.");
        RejectChange(value => ((JsonObject)Active(value)["progressSource"]!)["observedAt"] = null,
            "A source age cannot exist without its corresponding observation time.");
        RejectChange(value => value["fixture-private-body"] = "fixture-private-body",
            "An unknown private field is refused without echoing its value.");
        Reject(Raw(original.Replace("\"revision\":10", "\"revision\":10,\"revision\":10", StringComparison.Ordinal)),
            "Duplicate top-level JSON fields cannot override provenance.");
        Reject(Raw(original.Replace("\"nativeSlotId\":0", "\"nativeSlotId\":0,\"nativeSlotId\":0", StringComparison.Ordinal)),
            "Duplicate nested JSON fields cannot override a slot observation.");
        RejectChange(value => Rows(value).Add(Rows(value)[1]!.DeepClone()),
            "A repeated row cannot double-count entry or attempt provenance.");

        var many = Changed(value =>
        {
            value["rows"] = new JsonArray(Enumerable.Range(0, 128)
                .Select(index =>
                {
                    var row = Row("bounded-" + index, "bounded-lease-" + index);
                    row["execution"] = null; row["executionAttribution"] = "unavailable"; row["purpose"] = "unattributed";
                    row["budget"] = null; row["progress"] = "unknown"; row["processedInputTokens"] = null;
                    row["outputTokens"] = null; row["progressSource"] = Unavailable("provider-progress-unobserved");
                    return (JsonNode)row;
                }).ToArray());
            value["retainedCurrentRows"] = 128;
        });
        var maximum = Read(many);
        Check(maximum.Presentation.Summary.Length <= SlotStatusPresentation.MaximumPrimaryCharacters &&
              maximum.Presentation.Details.Contains("Returned current rows: 128; distinct physical lease IDs: 128", StringComparison.Ordinal) &&
              maximum.Presentation.Details.Length <= 32768 &&
              maximum.Presentation.Details.Contains("complete payload was validated", StringComparison.Ordinal),
            "A maximum row page is fully validated while its display details remain bounded and explicitly qualified.");
        ((JsonObject)Rows(many)[127]!)["accountingRevision"] = -1;
        Reject(Element(many), "Invalid provenance beyond the display-detail bound is still refused.");
        ((JsonObject)Rows(many)[127]!)["accountingRevision"] = 8;
        Rows(many).Add(Row("row-129", "lease-129")); many["retainedCurrentRows"] = 129;
        Reject(Element(many), "A 129th row is refused rather than silently truncated by the consumer.");
        Reject(Raw(original.Insert(1, new string(' ', 262144))),
            "An oversized raw payload is refused even when decoded fields would otherwise be valid.");
        var multibyte = Changed(value =>
        {
            var rows = new JsonArray();
            for (var index = 0; index < 64; index++)
            {
                var row = Row("utf8-" + index, "utf8-lease-" + index);
                var text = new string('\u754c', 180);
                row["correlationId"] = text; row["attemptId"] = index + text;
                var execution = (JsonObject)row["execution"]!;
                foreach (var field in new[] { "executionId", "profileId", "conversationId", "requestId", "runId", "effectId" })
                    execution[field] = text;
                rows.Add(row);
            }
            value["rows"] = rows; value["retainedCurrentRows"] = 64;
        }).ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Check(multibyte.Length < 262144 && Encoding.UTF8.GetByteCount(multibyte) > 262144,
            "The multibyte fixture isolates the UTF-8 payload bound from the character bound.");
        Reject(Raw(multibyte), "A character-bounded sample exceeding 256 KiB of UTF-8 is refused.");

        var invalidation = new JsonObject
        {
            ["schema"] = "cerebral.inference-observation.changed/1", ["originInstallationId"] = Owner,
            ["observerGenerationId"] = Generation, ["revision"] = 11
        };
        bool Event(JsonObject value, string owner = Owner, string knownGeneration = Generation, long knownRevision = 10) =>
            InferenceOwnershipProjection.TryReadInvalidation(Element(value), owner, knownGeneration, knownRevision, out _);
        Check(InferenceOwnershipProjection.TryReadInvalidation(Element(invalidation), Owner, Generation, 10, out var next) && next == 11,
            "A new same-owner, same-generation event yields only its invalidation revision.");
        invalidation["observerGenerationId"] = Generation.ToUpperInvariant();
        Check(Event(invalidation) && Event(invalidation, knownGeneration: Generation.ToUpperInvariant()),
            "Incoming and known UUID aliases match the same observer without bypassing revision validation.");
        invalidation["observerGenerationId"] = Generation;
        Check(!Event(invalidation, OtherOwner) && !Event(invalidation, knownGeneration: NextGeneration) &&
              !Event(invalidation, knownGeneration: "not-a-generation") && !Event(invalidation, knownRevision: 11),
            "Foreign-owner, different/malformed-generation and duplicate events do not invalidate a current sample.");
        invalidation["schema"] = "cerebral.inference-observation.changed/2";
        Check(!Event(invalidation), "An unknown invalidation schema is ignored.");
        invalidation["schema"] = "cerebral.inference-observation.changed/1";
        invalidation["revision"] = -1;
        Check(!Event(invalidation), "An invalid invalidation revision is ignored.");
        invalidation["revision"] = 11; invalidation["fixture-private-body"] = "fixture-private-body";
        Check(!Event(invalidation), "An invalidation with an extra payload body cannot become an observation.");
        return checks;
    }

    private static JsonElement Raw(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<int> CheckPollingAsync()
    {
        var checks = 0;
        void Check(bool value, string reason)
        {
            if (!value) throw new InvalidOperationException(reason);
            checks++;
        }
        static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

        var connection = new EqualConnection();
        var equalButDifferentConnection = new EqualConnection();
        using var state = new InferenceOwnershipPollState();
        state.Bind(connection, Owner);
        state.SetVisibility(windowVisible: true, minimized: false, statusVisible: true);
        Check(!state.Unsupported && !state.IsInFlight && state.ObserverGenerationId is null,
            "A newly bound owner has no inherited support or observer-generation claim.");
        Check(!state.Notify(Generation, 10) && !state.Notify(Guid.Empty.ToString("N"), 10),
            "An invalidation cannot establish an unknown observer generation.");
        Check(!state.TryBegin(equalButDifferentConnection, Owner, At(0), out _) &&
              !state.TryBegin(connection, OtherOwner, At(0), out _),
            "Polling binds exact connection object identity and the issuing owner, not object equality.");
        Check(state.TryBegin(connection, Owner, At(0), out var first) && first is not null && state.IsInFlight,
            "The first eligible visible sample begins immediately.");
        Check(!state.TryBegin(connection, Owner, At(.5), out _),
            "An outstanding read keeps request admission closed.");
        Check(state.Complete(first!, InferenceOwnershipReadOutcome.Sample, Generation, 10) &&
              !state.IsInFlight && !state.Unsupported && !state.Dirty &&
              state.ObserverGenerationId == Generation && state.Revision == 10,
            "Only a completed full sample establishes the observer generation and revision.");
        var tooSoon = !state.TryBegin(connection, Owner, At(.999), out _);
        var fallbackDue = state.TryBegin(connection, Owner, At(1), out var fallback);
        Check(tooSoon && fallbackDue,
            "A clean sample still receives a fallback full refresh, with at least one second between starts.");
        Check(!state.Notify(NextGeneration, 11) && !state.Notify(Generation, 10) && !state.Notify(Generation, 9),
            "Different-generation, duplicate and old invalidations do not dirty the current sample.");
        Check(state.Notify(Generation, 11) && state.Dirty && state.IsInFlight &&
              !state.TryBegin(connection, Owner, At(1.5), out _),
            "A new invalidation marks dirty without starting I/O or bypassing the in-flight fence.");
        Check(state.Complete(fallback!, InferenceOwnershipReadOutcome.Sample, Generation, 10) && state.Dirty,
            "An invalidation received during a read is retained when that read returns its older sample.");
        tooSoon = !state.TryBegin(connection, Owner, At(1.999), out _);
        var coalescedDue = state.TryBegin(connection, Owner, At(2), out var coalesced);
        Check(tooSoon && coalescedDue,
            "Dirty events cannot bypass the same monotonic start-spacing rule.");
        _ = state.Notify(Generation, 12);
        _ = state.Notify(Generation, 13);
        Check(state.Dirty && state.IsInFlight &&
              state.Complete(coalesced!, InferenceOwnershipReadOutcome.Sample, Generation, 13) &&
              state.Revision == 13,
            "An event burst retains one in-flight full read and adopts its current measured revision.");

        Check(state.TryBegin(connection, Owner, At(3), out var oldRevision) &&
              !state.Complete(oldRevision!, InferenceOwnershipReadOutcome.Sample, Generation, 12) &&
              state.Revision == 13 && !state.IsInFlight,
            "A stale full response cannot move a current generation's revision backwards.");
        Check(state.TryBegin(connection, Owner, At(4), out var invalidGeneration) &&
              !state.Complete(invalidGeneration!, InferenceOwnershipReadOutcome.Sample, Guid.Empty.ToString("N"), 14) &&
              state.ObserverGenerationId == Generation && state.Revision == 13,
            "An invalid generation cannot replace the established full sample.");
        Check(state.TryBegin(connection, Owner, At(5), out var replacement) &&
              state.Complete(replacement!, InferenceOwnershipReadOutcome.Sample, NextGeneration, 1) &&
              state.ObserverGenerationId == NextGeneration && state.Revision == 1,
            "A fresh full read may establish a replacement observer generation with its independent revision.");
        Check(state.TryBegin(connection, Owner, At(6), out var retired) &&
              !state.Complete(retired!, InferenceOwnershipReadOutcome.Sample, Generation, 1000) &&
              !state.Notify(Generation, 1001) && state.ObserverGenerationId == NextGeneration,
            "A retired observer generation cannot return through a late full response or invalidation.");

        state.SetVisibility(windowVisible: false, minimized: false, statusVisible: true);
        Check(!state.TryBegin(connection, Owner, At(7), out _),
            "A hidden window starts no inference observation.");
        state.SetVisibility(windowVisible: true, minimized: true, statusVisible: true);
        Check(!state.TryBegin(connection, Owner, At(7), out _),
            "A minimized window starts no inference observation.");
        state.SetVisibility(windowVisible: true, minimized: false, statusVisible: false);
        Check(!state.TryBegin(connection, Owner, At(7), out _),
            "A hidden status surface starts no inference observation.");
        state.SetVisibility(windowVisible: true, minimized: false, statusVisible: true);
        Check(state.TryBegin(connection, Owner, At(7), out var hidden),
            "Restoring the visible status surface admits its next due full read.");
        state.SetVisibility(windowVisible: false, minimized: false, statusVisible: true);
        state.SetVisibility(windowVisible: true, minimized: false, statusVisible: true);
        Check(!state.Complete(hidden!, InferenceOwnershipReadOutcome.Sample, NextGeneration, 2) &&
              state.Revision == 1 && !state.IsInFlight,
            "A read begun before a hide/show transition remains stale after the window is shown again.");

        // The TCS controls completion only. It models an outstanding read; it creates no
        // transport and makes no claim about the integration's CancellationToken wiring.
        Check(state.TryBegin(connection, Owner, At(8), out var priorEpoch),
            "The current epoch can start one controlled outstanding read.");
        var response = new TaskCompletionSource<InferenceOwnershipReadOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> FinishPriorEpochAsync() => state.Complete(priorEpoch!, await response.Task);
        var completing = FinishPriorEpochAsync();
        var nextConnection = new object();
        state.Bind(nextConnection, OtherOwner);
        Check(!state.Unsupported && state.ObserverGenerationId is null && state.IsInFlight &&
              !state.TryBegin(nextConnection, OtherOwner, At(9), out _),
            "Rebinding resets sample/support identity while preserving the one outstanding global read.");
        response.SetResult(InferenceOwnershipReadOutcome.Unsupported);
        Check(!await completing && !state.Unsupported && !state.IsInFlight,
            "An old epoch's unsupported reply cannot disable its replacement connection.");
        Check(state.TryBegin(nextConnection, OtherOwner, At(9), out var unsupported) &&
              state.Complete(unsupported!, InferenceOwnershipReadOutcome.Unsupported) && state.Unsupported &&
              !state.TryBegin(nextConnection, OtherOwner, At(9.5), out _),
            "A current explicitly classified unsupported result is cached only for its bound connection.");
        var reopenedConnection = new object();
        state.Bind(reopenedConnection, OtherOwner);
        tooSoon = !state.TryBegin(reopenedConnection, OtherOwner, At(9.999), out _);
        var reopenedDue = state.TryBegin(reopenedConnection, OtherOwner, At(10), out var unavailable);
        Check(!state.Unsupported && state.ObserverGenerationId is null && tooSoon && reopenedDue,
            "A new epoch clears unsupported discovery but preserves the last global request-start time.");
        var unavailableAccepted = state.Complete(unavailable!, InferenceOwnershipReadOutcome.Unavailable);
        var retryDue = state.TryBegin(reopenedConnection, OtherOwner, At(11), out var retry);
        Check(unavailableAccepted && !state.Unsupported && retryDue,
            "An ordinary unavailable result remains retryable and does not impersonate unsupported discovery.");
        Check(state.Complete(retry!, InferenceOwnershipReadOutcome.Sample, Generation, 1) &&
              state.ObserverGenerationId == Generation && state.Revision == 1,
            "A new connection epoch may establish its own sample independently of retired prior-epoch generations.");
        Check(state.TryBegin(reopenedConnection, OtherOwner, At(12), out var disposed),
            "One final controlled read is outstanding before disposal.");
        state.Dispose();
        Check(!state.Complete(disposed!, InferenceOwnershipReadOutcome.Sample, Generation, 2) &&
              !state.IsInFlight && !state.TryBegin(reopenedConnection, OtherOwner, At(13), out _),
            "Disposal fences late completion and prevents any new polling admission.");

        using var alias = new InferenceOwnershipPollState();
        alias.Bind(connection, Owner); alias.SetVisibility(true, false, true);
        Check(alias.TryBegin(connection, Owner, At(0), out var canonical) &&
              alias.Complete(canonical!, InferenceOwnershipReadOutcome.Sample, Generation, 10),
            "The UUID-alias fixture first establishes its actual observer generation.");
        Check(alias.TryBegin(connection, Owner, At(1), out var olderAlias) &&
              !alias.Complete(olderAlias!, InferenceOwnershipReadOutcome.Sample, Generation.ToUpperInvariant(), 9) &&
              alias.ObserverGenerationId == Generation && alias.Revision == 10,
            "Changing UUID letter case cannot disguise a revision rollback as a replacement observer.");
        Check(alias.Notify(Generation.ToUpperInvariant(), 11) &&
              alias.TryBegin(connection, Owner, At(2), out var newerAlias) &&
              alias.Complete(newerAlias!, InferenceOwnershipReadOutcome.Sample, Generation.ToUpperInvariant(), 11) &&
              alias.ObserverGenerationId == Generation && alias.Revision == 11,
            "Valid UUID aliases share canonical generation identity for events and full samples.");

        using var cached = new InferenceOwnershipPollState();
        cached.Bind(connection, Owner); cached.SetVisibility(true, false, true);
        Check(cached.TryBegin(connection, Owner, At(0), out var unsupportedCache) &&
              cached.Complete(unsupportedCache!, InferenceOwnershipReadOutcome.Unsupported) &&
              !cached.TryBegin(connection, Owner, At(100), out _) && cached.Unsupported,
            "An explicitly unsupported current connection is not polled again even after the fallback interval has elapsed.");
        return checks;
    }

    private sealed class EqualConnection
    {
        public override bool Equals(object? other) => other is EqualConnection;
        public override int GetHashCode() => 1;
    }

    private static JsonObject Fixture() => new()
    {
        ["schema"] = "cerebral.inference-ownership/1", ["issuingInstallationId"] = Owner,
        ["ownerConnectionRegistrationFence"] = null, ["ownerConnectionRegistrationFenceState"] = "unavailable",
        ["ownerConnectionRegistrationFenceReason"] = "owner-connection-registration-fence-not-captured",
        ["observerGenerationId"] = Generation, ["revision"] = 10, ["sampledAt"] = Stamp,
        ["accountingGenerationId"] = Accounting, ["accountingCoverageComplete"] = true,
        ["state"] = "observed", ["partial"] = false, ["truncated"] = false,
        ["maximumRows"] = 128, ["retainedCurrentRows"] = 1, ["reasons"] = new JsonArray(),
        ["counts"] = new JsonObject
        {
            ["observedKnownWorkingProfileIdentities"] = 1, ["completeWorkingProfileIdentities"] = 1,
            ["observedKnownExecutions"] = 2, ["completeExecutions"] = 2, ["completeContextCount"] = null,
            ["source"] = Source("observed", Scheduler, null, 30)
        },
        ["modelCoverage"] = Source("observed", Runtime, 7, 40),
        ["models"] = new JsonArray(Model()),
        ["rows"] = new JsonArray(Row("released-a", new string('a', 32), released: true), Row("active-b", new string('b', 32))),
        ["remoteState"] = "unsupported", ["eventDeliveryState"] = "available",
        ["executionAdmissionOpen"] = null, ["modelAdmissionOpen"] = null,
        ["admissionSource"] = Unavailable("combined-admission-policy-not-captured"),
        ["semantics"] = "Issuing local owner only. Profiles, accepted executions, exact leases and reusable native slots are distinct. Terminal is not release or task/build success."
    };

    private static JsonObject Model() => new()
    {
        ["modelId"] = "fixture-model", ["instanceId"] = Instance, ["runtimeGenerationId"] = Runtime,
        ["runtimeEpoch"] = 7, ["state"] = "startup-captured", ["runningLaunchSlotMaximum"] = 2,
        ["effectiveContextTokens"] = 8192, ["launchBindingSha256"] = new string('a', 64),
        ["launchSource"] = Source("captured", Runtime, 6, 50),
        ["managedAllocatedSlots"] = 2, ["managedAvailableSlots"] = 1, ["managedQuarantinedSlots"] = 0,
        ["allocatorCoverageComplete"] = true, ["allocatorSource"] = Source("observed", Generation, 9, 20),
        ["providerObservedCapacity"] = null, ["providerCapacitySource"] = Unavailable("native-slot-capacity-not-captured"),
        ["providerObservedBusySlots"] = null, ["providerActivitySource"] = Unavailable("provider-activity-unobserved")
    };

    private static JsonObject Row(string id, string lease, bool released = false) => new()
    {
        ["rowId"] = Accounting + ":" + id, ["accountingEntryId"] = id,
        ["accountingGenerationId"] = Accounting, ["accountingRevision"] = 8,
        ["originInstallationId"] = Owner, ["kind"] = "LocalPreparation", ["modelId"] = "fixture-model",
        ["instanceId"] = Instance, ["correlationId"] = "correlation-" + id, ["attemptId"] = "attempt-" + id,
        ["purpose"] = "task-generation", ["execution"] = new JsonObject
        {
            ["executionId"] = "execution-" + id, ["profileId"] = "fixture-profile", ["profileRevision"] = 3,
            ["conversationId"] = "fixture-conversation", ["requestId"] = "request-" + id,
            ["runId"] = "run-" + id, ["effectId"] = "effect-" + id
        },
        ["executionAttribution"] = "local-host-invocation", ["accountingLeaseId"] = null,
        ["reservationId"] = null, ["leaseId"] = lease, ["nativeSlotId"] = 0,
        ["phase"] = released ? "released" : "generating", ["cancellationRequested"] = false,
        ["uncertaintyCode"] = null,
        ["budget"] = new JsonObject
        {
            ["inputTokens"] = 900, ["effectiveContext"] = 8192, ["desiredResponse"] = 1024,
            ["response"] = 1024, ["reasoningReserve"] = 128, ["safetyReserve"] = 128,
            ["countBasis"] = "exact-native-template-token-ids"
        },
        ["terminal"] = released ? new JsonObject
        {
            ["finish"] = "stop", ["cancellationConfirmed"] = false, ["providerError"] = false,
            ["evaluatedTokens"] = 900, ["generatedTokens"] = 25,
            ["generatedTokenScope"] = "provider-unspecified", ["reasoningTokens"] = null, ["at"] = Stamp
        } : null,
        ["cache"] = null, ["progress"] = released ? "unknown" : "output",
        ["processedInputTokens"] = released ? null : JsonValue.Create(900),
        ["outputTokens"] = released ? null : JsonValue.Create(25),
        ["progressSource"] = released ? Unavailable("provider-progress-unobserved") : Source("observed", Accounting, null, 10),
        ["release"] = released ? "confirmed" : "unobserved",
        ["releaseProof"] = released ? "accounting-owner-confirmed-retirement" : null,
        ["releasedAt"] = released ? Stamp : null, ["observedAt"] = Stamp
    };

    private static JsonObject Source(string state, string generation, long? revision, long age) => new()
    {
        ["state"] = state, ["reason"] = null,
        ["observedAt"] = DateTimeOffset.Parse(Stamp, System.Globalization.CultureInfo.InvariantCulture)
            .AddMilliseconds(-age).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        ["generationId"] = generation, ["revision"] = revision, ["ageMilliseconds"] = age
    };

    private static JsonObject Unavailable(string reason) => new()
    {
        ["state"] = "unavailable", ["reason"] = reason, ["observedAt"] = null,
        ["generationId"] = null, ["revision"] = null, ["ageMilliseconds"] = null
    };
}
