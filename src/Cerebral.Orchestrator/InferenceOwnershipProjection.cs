using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Cerebral.Orchestrator;

internal sealed record InferenceOwnershipPresentation(string Summary, string Details)
{
    internal static InferenceOwnershipPresentation Unavailable(string reason)
    {
        var publicReason = string.IsNullOrWhiteSpace(reason) || reason.Length > 256 || reason.Any(char.IsControl)
            ? "No valid issuing-owner observation is available." : reason;
        return new("Slot occupants unobserved", publicReason +
            "\nAccepted work, physical leases and source ages remain unknown until a valid observation is read.");
    }
}

internal sealed record InferenceOwnershipSample(string ObserverGenerationId, long Revision,
    DateTimeOffset SampledAt, InferenceOwnershipPresentation Presentation);

/// <summary>Reads the issuing owner's captured schema, without importing Worker types or inferring admission.</summary>
internal static class InferenceOwnershipProjection
{
    private const int MaximumPayloadBytes = 262144;
    private const int MaximumDetailsCharacters = 32768;
    private static readonly string[] SnapshotFields =
    [
        "schema", "issuingInstallationId", "ownerConnectionRegistrationFence", "ownerConnectionRegistrationFenceState",
        "ownerConnectionRegistrationFenceReason", "observerGenerationId", "revision", "sampledAt", "accountingGenerationId",
        "accountingCoverageComplete", "state", "partial", "truncated", "maximumRows", "retainedCurrentRows", "reasons",
        "counts", "modelCoverage", "models", "rows", "remoteState", "eventDeliveryState", "executionAdmissionOpen",
        "modelAdmissionOpen", "admissionSource", "semantics"
    ];
    private static readonly string[] SourceFields = ["state", "reason", "observedAt", "generationId", "revision", "ageMilliseconds"];
    private static readonly string[] RowFields =
    [
        "rowId", "accountingEntryId", "accountingGenerationId", "accountingRevision", "originInstallationId", "kind",
        "modelId", "instanceId", "correlationId", "attemptId", "purpose", "execution", "executionAttribution",
        "accountingLeaseId", "reservationId", "leaseId", "nativeSlotId", "phase", "cancellationRequested", "uncertaintyCode",
        "budget", "terminal", "cache", "progress", "processedInputTokens", "outputTokens", "progressSource", "release",
        "releaseProof", "releasedAt", "observedAt"
    ];
    private static readonly string[] ModelFields =
    [
        "modelId", "instanceId", "runtimeGenerationId", "runtimeEpoch", "state", "runningLaunchSlotMaximum",
        "effectiveContextTokens", "launchBindingSha256", "launchSource", "managedAllocatedSlots", "managedAvailableSlots",
        "managedQuarantinedSlots", "allocatorCoverageComplete", "allocatorSource", "providerObservedCapacity",
        "providerCapacitySource", "providerObservedBusySlots", "providerActivitySource"
    ];

    internal static InferenceOwnershipSample ReadSample(JsonElement value, string expectedInstallationId,
        IReadOnlyList<SlotAgentLabel>? agentLabels = null)
    {
        try { return Read(value, expectedInstallationId, agentLabels); }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException or JsonException)
        { throw Invalid(); }
    }

    private static InferenceOwnershipSample Read(JsonElement value, string expectedInstallationId, IReadOnlyList<SlotAgentLabel>? agentLabels)
    {
        Require(ValidText(expectedInstallationId, 256));
        CheckSize(value);
        var envelope = Fields(value, SnapshotFields);
        Equal(envelope["schema"], "cerebral.inference-ownership/1");
        Equal(envelope["issuingInstallationId"], expectedInstallationId);
        var generation = CanonicalGeneration(Text(envelope["observerGenerationId"]));
        var revision = Integer64(envelope["revision"], 1);
        var sampledAt = Timestamp(envelope["sampledAt"]);
        var accountingGeneration = Text(envelope["accountingGenerationId"]);
        var accountingComplete = Boolean(envelope["accountingCoverageComplete"]);
        var partial = Boolean(envelope["partial"]);
        var truncated = Boolean(envelope["truncated"]);
        Equal(envelope["state"], partial ? "partial" : "observed");
        var maximumRows = Integer(envelope["maximumRows"], 1, 128);
        var retained = Integer(envelope["retainedCurrentRows"], 0, 4096);
        var reasons = Array(envelope["reasons"], 16).Select(item => Text(item)).ToArray();
        Require(reasons.Distinct(StringComparer.Ordinal).Count() == reasons.Length && partial == (reasons.Length != 0));
        Require(!truncated || partial);
        Require(accountingComplete || partial);

        // These fields are explicitly unavailable in this schema. A local sample is not a connection-registration fence.
        Null(envelope["ownerConnectionRegistrationFence"]);
        Equal(envelope["ownerConnectionRegistrationFenceState"], "unavailable");
        Equal(envelope["ownerConnectionRegistrationFenceReason"], "owner-connection-registration-fence-not-captured");
        Equal(envelope["remoteState"], "unsupported");
        var eventState = Choice(envelope["eventDeliveryState"], "available", "unavailable", "unsupported");
        Null(envelope["executionAdmissionOpen"]);
        Null(envelope["modelAdmissionOpen"]);
        var admission = Source(envelope["admissionSource"]);
        Require(admission.State == "unavailable");
        _ = Text(envelope["semantics"], 2048); // Validate the bounded field; never display server-supplied prose as UI policy.

        var counts = Fields(envelope["counts"], ["observedKnownWorkingProfileIdentities", "completeWorkingProfileIdentities",
            "observedKnownExecutions", "completeExecutions", "completeContextCount", "source"]);
        var profiles = Integer(counts["observedKnownWorkingProfileIdentities"], 0, 256);
        var executions = Integer(counts["observedKnownExecutions"], 0, 256);
        var completeProfiles = NullableInteger(counts["completeWorkingProfileIdentities"], 256);
        var completeExecutions = NullableInteger(counts["completeExecutions"], 256);
        var countSource = Source(counts["source"]);
        Null(counts["completeContextCount"]);
        Require(profiles <= executions && (partial
            ? completeProfiles is null && completeExecutions is null && countSource.State == "partial"
            : completeProfiles == profiles && completeExecutions == executions && countSource.State == "observed"));
        var modelCoverage = Source(envelope["modelCoverage"]);

        var modelLines = new List<string>();
        var instances = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in Array(envelope["models"], 64))
            ReadModel(model, instances, modelLines);

        var rowLines = new List<string>();
        var occupants = new List<SlotStatusOccupant>();
        var retainedContexts = new List<SlotRetainedContext>();
        var imageVerifications = new List<SlotImageVerificationObservation>();
        var unmappedSlots = 0;
        var rows = new HashSet<string>(StringComparer.Ordinal);
        var entries = new HashSet<string>(StringComparer.Ordinal);
        var attempts = new HashSet<string>(StringComparer.Ordinal);
        var currentLeases = new HashSet<string>(StringComparer.Ordinal);
        var releasedLeases = new HashSet<string>(StringComparer.Ordinal);
        var currentRows = 0;
        var releasedRows = 0;
        var withoutLease = 0;
        var uncertainRows = 0;
        foreach (var row in Array(envelope["rows"], maximumRows))
        {
            var captured = ReadRow(row, expectedInstallationId, accountingGeneration, rows, entries, attempts, rowLines);
            if (captured.Released)
            { releasedRows++; if (captured.LeaseId is { } lease) releasedLeases.Add(lease); }
            else
            {
                currentRows++;
                if (captured.LeaseId is { } lease) currentLeases.Add(lease); else withoutLease++;
            }
            if (captured.Uncertain) uncertainRows++;
            if (captured.Occupant is { } occupant) occupants.Add(occupant);
            if (captured.RetainedContext is { } context) retainedContexts.Add(context);
            if (captured.ImageVerification is { } image) imageVerifications.Add(image);
            if (captured.UnmappedSlot) unmappedSlots++;
        }
        Require(currentRows <= retained && (truncated || currentRows == retained));

        // Count exact lease identities, never a slot number or an accounting/reservation identity.
        // Even a complete returned page does not establish native/provider/context occupancy.
        var summary = SlotStatusPresentation.PrimaryCaption(occupants, retainedContexts, unmappedSlots, partial, imageVerifications, agentLabels);
        var details = new DetailsWriter();
        foreach (var line in SlotStatusPresentation.OccupantLines(occupants, unmappedSlots, partial, sampledAt, retainedContexts, imageVerifications, agentLabels))
            details.Add(line);
        details.Add("Accepted agents and executions below are scheduler-owned work, not slot capacity or proof of generation.");
        details.Add($"Accepted local work: {Number(profiles)} known profile identities; {Number(executions)} known executions. " +
            (partial ? "Complete accepted-work totals are unknown." : "Complete within the captured local scheduler coverage."));
        details.Add("Allocator and ownership pages are separate observations. They are not combined into an atomic free/occupied map.");
        details.Add("Captured provenance and source ages:");
        details.Add($"Issuing owner: {expectedInstallationId}; observer: {generation}; revision: {Number(revision)}; sampled: {When(sampledAt)}.");
        details.Add(SourceLine("Accepted-work source", countSource));
        details.Add($"Accounting generation: {accountingGeneration}; coverage: {(accountingComplete ? "complete" : "incomplete")}; " +
            $"retained current rows: {Number(retained)}; returned: {Number(rows.Count)}; truncated: {Yes(truncated)}.");
        details.Add($"Returned current rows: {Number(currentRows)}; distinct physical lease IDs: {Number(currentLeases.Count)}; " +
            $"rows without a captured physical lease: {Number(withoutLease)}; unknown/quarantined/unresolved rows: {Number(uncertainRows)}.");
        details.Add($"Returned confirmed-release history: {Number(releasedRows)} rows, {Number(releasedLeases.Count)} distinct physical lease IDs. " +
            "Release confirms ownership retirement, not workflow, build or task success.");
        if (reasons.Length != 0) details.Add("Incomplete coverage: " + string.Join(", ", reasons) + ".");
        details.Add(SourceLine("Model coverage", modelCoverage));
        details.Add($"Captured model instances: {Number(instances.Count)}; invalidation delivery: {eventState}. Source ages and generations are independent.");
        details.Add("Physical lease counts cover only returned current rows. Reusable native slot IDs are locators, not lease identities. " +
            "Allocator counters are sampled independently; they do not reserve work or establish admission or fit.");
        details.Add("Connection registration fencing, remote totals, exact context totals, provider activity and per-slot KV bytes remain unobserved. " +
            "Running-launch settings describe that launch, not current configured limits. Terminal results are distinct from confirmed release.");
        details.Add(SourceLine("Admission policy", admission));
        foreach (var line in modelLines) details.Add(line);
        foreach (var line in rowLines) details.Add(line);
        return new(generation, revision, sampledAt, new(summary, details.ToString()));
    }

    // Events only dirty a current, same-owner/same-generation observation. No payload can become a sample.
    internal static bool TryReadInvalidation(JsonElement value, string expectedOwner, string knownGeneration,
        long knownRevision, out long revision)
    {
        revision = 0;
        try
        {
            Require(ValidText(expectedOwner, 256) && ValidText(knownGeneration, 256) && knownRevision >= 1);
            var known = CanonicalGeneration(knownGeneration);
            CheckSize(value);
            var fields = Fields(value, ["schema", "originInstallationId", "observerGenerationId", "revision"]);
            Equal(fields["schema"], "cerebral.inference-observation.changed/1");
            Equal(fields["originInstallationId"], expectedOwner);
            Require(CanonicalGeneration(Text(fields["observerGenerationId"])) == known);
            var next = Integer64(fields["revision"], 1);
            if (next <= knownRevision) return false;
            revision = next;
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or FormatException or OverflowException or JsonException)
        { return false; }
    }

    private static void ReadModel(JsonElement value, HashSet<string> instances, List<string> lines)
    {
        var fields = Fields(value, ModelFields);
        var model = Text(fields["modelId"]);
        var instance = Text(fields["instanceId"]);
        Require(instances.Add(instance)); // A model may have multiple separately captured instances.
        var generation = Text(fields["runtimeGenerationId"]);
        var epoch = Integer64(fields["runtimeEpoch"]);
        var state = Choice(fields["state"], "captured", "startup-captured");
        var launchSlots = NullableInteger(fields["runningLaunchSlotMaximum"]);
        var context = NullableInteger(fields["effectiveContextTokens"]);
        var binding = NullableText(fields["launchBindingSha256"], 64);
        Require(binding is null || binding.Length == 64 && binding.All(Uri.IsHexDigit));
        var launchSource = Source(fields["launchSource"]);
        var allocated = NullableInteger(fields["managedAllocatedSlots"]);
        var available = NullableInteger(fields["managedAvailableSlots"]);
        var quarantined = NullableInteger(fields["managedQuarantinedSlots"]);
        var allocatorComplete = Boolean(fields["allocatorCoverageComplete"]);
        Require(!allocatorComplete || allocated is not null && available is not null && quarantined is not null);
        var allocatorSource = Source(fields["allocatorSource"]);
        var providerCapacity = NullableInteger(fields["providerObservedCapacity"]);
        var providerCapacitySource = Source(fields["providerCapacitySource"]);
        var providerBusy = NullableInteger(fields["providerObservedBusySlots"]);
        var providerActivitySource = Source(fields["providerActivitySource"]);
        lines.Add($"Model {model}; instance {instance}; runtime {generation}/{Number(epoch)}; state {state}. " +
            $"Captured running-launch slot maximum {Number(launchSlots)}; effective context tokens {Number(context)}; binding {binding ?? "unknown"}.");
        lines.Add(SourceLine("  Launch source", launchSource));
        lines.Add($"  Managed allocator: allocated {Number(allocated)}; available {Number(available)}; quarantined {Number(quarantined)}; " +
            $"coverage complete {Yes(allocatorComplete)} (independent samples, not atomic admission).");
        lines.Add(SourceLine("  Allocator source", allocatorSource));
        lines.Add($"  Provider-reported capacity {Number(providerCapacity)}; busy slots {Number(providerBusy)}.");
        lines.Add(SourceLine("  Provider capacity source", providerCapacitySource));
        lines.Add(SourceLine("  Provider activity source", providerActivitySource));
    }

    private sealed record RowObservation(string? LeaseId, bool Released, bool Uncertain,
        SlotStatusOccupant? Occupant, bool UnmappedSlot, SlotRetainedContext? RetainedContext,
        SlotImageVerificationObservation? ImageVerification);

    private static RowObservation ReadRow(JsonElement value, string owner, string accountingGeneration,
        HashSet<string> rows, HashSet<string> entries, HashSet<string> attempts, List<string> lines)
    {
        var fields = Fields(value, RowFields);
        var row = Text(fields["rowId"], 513);
        var entry = Text(fields["accountingEntryId"]);
        Equal(fields["accountingGenerationId"], accountingGeneration);
        Require(row == accountingGeneration + ":" + entry && rows.Add(row) && entries.Add(entry));
        var revision = Integer64(fields["accountingRevision"]);
        var origin = NullableText(fields["originInstallationId"]);
        Require(origin is null || origin == owner);
        var kind = Choice(fields["kind"], "LocalLoad", "LocalContext", "LocalPreparation", "LocalMeasurement", "ImageVerification",
            "CapabilityLease", "OutboundPreparation", "OutboundMeasurement", "OutboundGeneration", "RemoteModelChange", "InboundReservation", "FittingProcess");
        var model = Text(fields["modelId"]);
        var instance = NullableText(fields["instanceId"]);
        var correlation = Text(fields["correlationId"], 513);
        var attempt = Text(fields["attemptId"]);
        Require(attempts.Add(attempt));
        var purpose = Choice(fields["purpose"], "task-generation", "compaction", "unattributed");
        var attribution = Choice(fields["executionAttribution"], "unavailable", "local-host-invocation");
        Require((attribution == "unavailable") == (fields["execution"].ValueKind == JsonValueKind.Null));
        var execution = ReadExecution(fields["execution"], out var profileId, out var profileRevision, out var runId);
        var accountingLease = NullableText(fields["accountingLeaseId"]);
        var reservation = NullableText(fields["reservationId"]);
        var lease = NullableText(fields["leaseId"]);
        var slot = NullableInteger(fields["nativeSlotId"]);
        Require(slot is null || lease is not null);
        var phase = Choice(fields["phase"], "queued", "loading", "preparing", "ready", "generating", "cancelling", "terminal",
            "releasing", "stopping", "released", "quarantined", "unknown");
        var cancellation = Boolean(fields["cancellationRequested"]);
        var uncertainty = NullableText(fields["uncertaintyCode"]);
        Require(uncertainty is null or "ownership-unresolved");
        var budget = ReadBudget(fields["budget"]);
        var terminal = ReadTerminal(fields["terminal"]);
        var cache = ReadCache(fields["cache"]);
        var progress = Choice(fields["progress"], "prefill", "output", "unknown");
        var input = NullableInteger(fields["processedInputTokens"]);
        var output = NullableInteger(fields["outputTokens"]);
        var progressSource = Source(fields["progressSource"]);
        var released = Choice(fields["release"], "unobserved", "confirmed") == "confirmed";
        var proof = NullableText(fields["releaseProof"]);
        var releasedAt = NullableTimestamp(fields["releasedAt"]);
        Require(released ? phase == "released" && releasedAt is not null &&
            proof is "accounting-owner-confirmed-retirement" or "exact-instance-stopped" or "no-provider-effect"
            : phase != "released" && releasedAt is null && proof is null);
        var observedAt = Timestamp(fields["observedAt"]);
        lines.Add($"Row {row}; accounting revision {Number(revision)}; origin {origin ?? "unobserved"}; {kind}; " +
            $"model {model}; instance {instance ?? "unobserved"}; phase {phase}; observed {When(observedAt)}.");
        lines.Add($"  Correlation {correlation}; attempt {attempt}; purpose {purpose}; execution attribution {attribution}; {execution}");
        lines.Add($"  Physical lease {lease ?? "unobserved"}; reusable native slot {Number(slot)}; " +
            $"accounting lease {accountingLease ?? "unobserved"}; reservation {reservation ?? "unobserved"}.");
        lines.Add($"  Cancellation requested {Yes(cancellation)}; uncertainty {uncertainty ?? "none captured"}; " +
            $"release {(released ? "confirmed" : "unobserved")}; proof {proof ?? "unobserved"}; released at {When(releasedAt)}.");
        lines.Add($"  Progress {progress}; processed input tokens {Number(input)}; output tokens {Number(output)}.");
        lines.Add(SourceLine("  Progress source", progressSource));
        if (budget is not null) lines.Add("  " + budget);
        if (terminal is not null) lines.Add("  " + terminal);
        if (cache is not null) lines.Add("  " + cache);
        var uncertain = phase is "unknown" or "quarantined" || uncertainty is not null;
        SlotStatusOccupant? occupant = !released && instance is not null && lease is not null && slot is >= 0 and < 16
            ? new(model, instance, slot.Value, lease, attempt, profileId, profileRevision, runId,
                phase, uncertain, progress, progressSource.State, progressSource.AgeMilliseconds, observedAt, kind, purpose)
            : null;
        // A context record without a mapped physical lease is retained evidence, not proof of idle or free capacity.
        var retainedContext = !released && kind == "LocalContext" && occupant is null
            ? new SlotRetainedContext(model, instance, profileId, profileRevision, runId, phase, uncertain, observedAt) : null;
        // An image check without the exact physical lease remains an instance-level observation.
        // Never place it on a slot using its model or instance identity alone.
        var imageVerification = !released && kind == "ImageVerification" && occupant is null
            ? new SlotImageVerificationObservation(model, instance, phase, uncertain, observedAt) : null;
        return new(lease, released, uncertain, occupant,
            !released && kind != "ImageVerification" && (lease is not null || slot is not null) && occupant is null,
            retainedContext, imageVerification);
    }

    private static string ReadExecution(JsonElement value, out string? profileId, out int? profileRevision, out string? runId)
    {
        profileId = null; profileRevision = null; runId = null;
        if (value.ValueKind == JsonValueKind.Null) return "accepted execution coordinate unavailable.";
        var fields = Fields(value, ["executionId", "profileId", "profileRevision", "conversationId", "requestId", "runId", "effectId"]);
        var id = Text(fields["executionId"]);
        var profile = Text(fields["profileId"]);
        var revision = Integer(fields["profileRevision"]);
        var conversation = NullableText(fields["conversationId"]);
        var request = NullableText(fields["requestId"]);
        var run = NullableText(fields["runId"]);
        var effect = NullableText(fields["effectId"]);
        profileId = profile; profileRevision = revision; runId = run;
        // One accepted execution/profile may legitimately occur in several inference attempts.
        return $"execution {id}; captured profile {profile}@{Number(revision)}; conversation {conversation ?? "unavailable"}; " +
            $"request {request ?? "unavailable"}; run {run ?? "unavailable"}; effect {effect ?? "unavailable"}.";
    }

    private static string? ReadBudget(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        var fields = Fields(value, ["inputTokens", "effectiveContext", "desiredResponse", "response", "reasoningReserve", "safetyReserve", "countBasis"]);
        var input = Integer(fields["inputTokens"]);
        var context = Integer(fields["effectiveContext"]);
        var desired = Integer(fields["desiredResponse"]);
        var response = Integer(fields["response"]);
        var reasoning = Integer(fields["reasoningReserve"]);
        var safety = Integer(fields["safetyReserve"]);
        var basis = Choice(fields["countBasis"], "exact-native-template-token-ids", "provider-declared-unclassified");
        return $"Prepared token budget: input {Number(input)}; effective context {Number(context)}; desired response {Number(desired)}; " +
            $"response {Number(response)}; reasoning reserve {Number(reasoning)}; safety reserve {Number(safety)}; basis {basis}.";
    }

    private static string? ReadTerminal(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        var fields = Fields(value, ["finish", "cancellationConfirmed", "providerError", "evaluatedTokens", "generatedTokens", "generatedTokenScope", "reasoningTokens", "at"]);
        var finish = Choice(fields["finish"], "stop", "length", "cancelled", "unknown");
        var cancellation = Boolean(fields["cancellationConfirmed"]);
        var providerError = Boolean(fields["providerError"]);
        var evaluated = NullableInteger(fields["evaluatedTokens"]);
        var generated = NullableInteger(fields["generatedTokens"]);
        Equal(fields["generatedTokenScope"], "provider-unspecified");
        Null(fields["reasoningTokens"]);
        var at = Timestamp(fields["at"]);
        return $"Terminal inference result {finish} at {When(at)}; cancellation confirmed {Yes(cancellation)}; provider error {Yes(providerError)}; " +
            $"evaluated tokens {Number(evaluated)}; generated tokens {Number(generated)} (provider-unspecified scope); reasoning tokens unobserved.";
    }

    private static string? ReadCache(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        var fields = Fields(value, ["preparedInputTokens", "cachedTokens", "processedTokens", "promptMilliseconds"]);
        var prepared = Integer(fields["preparedInputTokens"]);
        var cached = NullableInteger(fields["cachedTokens"]);
        var processed = NullableInteger(fields["processedTokens"]);
        double? milliseconds = null;
        if (fields["promptMilliseconds"].ValueKind != JsonValueKind.Null)
        {
            Require(fields["promptMilliseconds"].ValueKind == JsonValueKind.Number &&
                fields["promptMilliseconds"].TryGetDouble(out var number) && double.IsFinite(number) && number >= 0);
            milliseconds = fields["promptMilliseconds"].GetDouble();
        }
        return $"Provider prompt-cache telemetry: prepared tokens {Number(prepared)}; cached tokens {Number(cached)}; " +
            $"processed tokens {Number(processed)}; prompt milliseconds {milliseconds?.ToString("G", CultureInfo.InvariantCulture) ?? "unobserved"}. " +
            "Token telemetry does not measure resident KV memory.";
    }

    private sealed record CaptureSource(string State, string? Reason, DateTimeOffset? ObservedAt,
        string? GenerationId, long? Revision, long? AgeMilliseconds);

    private static CaptureSource Source(JsonElement value)
    {
        var fields = Fields(value, SourceFields);
        var state = Choice(fields["state"], "observed", "captured", "partial", "unavailable", "stale");
        var reason = NullableText(fields["reason"], 512);
        var observed = NullableTimestamp(fields["observedAt"]);
        var generation = NullableText(fields["generationId"]);
        var revision = fields["revision"].ValueKind == JsonValueKind.Null ? (long?)null : Integer64(fields["revision"]);
        var age = fields["ageMilliseconds"].ValueKind == JsonValueKind.Null ? (long?)null : Integer64(fields["ageMilliseconds"]);
        Require((observed is null) == (age is null));
        Require(state is not ("observed" or "captured" or "stale") || observed is not null);
        Require(state != "unavailable" || reason is not null);
        return new(state, reason, observed, generation, revision, age);
    }

    private static string SourceLine(string label, CaptureSource source) =>
        $"{label}: {source.State}; observed {When(source.ObservedAt)}; age {Number(source.AgeMilliseconds)} ms; " +
        $"generation {source.GenerationId ?? "unobserved"}; revision {Number(source.Revision)}; reason {source.Reason ?? "none captured"}.";

    private static Dictionary<string, JsonElement> Fields(JsonElement value, string[] expected)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            Require(System.Array.IndexOf(expected, property.Name) >= 0 && fields.TryAdd(property.Name, property.Value));
        }
        Require(fields.Count == expected.Length);
        return fields;
    }

    private static JsonElement.ArrayEnumerator Array(JsonElement value, int maximum)
    {
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= maximum);
        return value.EnumerateArray();
    }

    private static void CheckSize(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        var json = value.GetRawText();
        Require(json.Length <= MaximumPayloadBytes && Encoding.UTF8.GetByteCount(json) <= MaximumPayloadBytes);
    }

    private static bool ValidText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

    private static string Text(JsonElement value, int maximum = 256)
    {
        Require(value.ValueKind == JsonValueKind.String);
        var text = value.GetString();
        Require(ValidText(text, maximum));
        return text!;
    }

    private static string? NullableText(JsonElement value, int maximum = 256) =>
        value.ValueKind == JsonValueKind.Null ? null : Text(value, maximum);
    private static string CanonicalGeneration(string value)
    {
        Require(value.Length == 32);
        Require(Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty);
        return id.ToString("N");
    }
    private static void Equal(JsonElement value, string expected) => Require(Text(value, Math.Max(256, expected.Length)) == expected);
    private static void Null(JsonElement value) => Require(value.ValueKind == JsonValueKind.Null);
    private static string Choice(JsonElement value, params string[] allowed)
    {
        var text = Text(value);
        Require(System.Array.IndexOf(allowed, text) >= 0);
        return text;
    }

    private static int Integer(JsonElement value, int minimum = 0, int maximum = int.MaxValue)
    {
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= minimum && number <= maximum);
        return value.GetInt32();
    }

    private static int? NullableInteger(JsonElement value, int maximum = int.MaxValue) =>
        value.ValueKind == JsonValueKind.Null ? null : Integer(value, 0, maximum);
    private static long Integer64(JsonElement value, long minimum = 0)
    {
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= minimum);
        return value.GetInt64();
    }

    private static bool Boolean(JsonElement value)
    {
        Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False);
        return value.GetBoolean();
    }

    private static DateTimeOffset Timestamp(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out _));
        return value.GetDateTimeOffset();
    }

    private static DateTimeOffset? NullableTimestamp(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : Timestamp(value);
    private static string Number(long? number) => number?.ToString(CultureInfo.InvariantCulture) ?? "unobserved";
    private static string When(DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture) ?? "unobserved";
    private static string Yes(bool value) => value ? "yes" : "no";
    private static void Require(bool condition) { if (!condition) throw Invalid(); }
    private static InvalidDataException Invalid() => new("The inference-ownership observation is invalid or does not match its issuing owner.");

    private sealed class DetailsWriter
    {
        private readonly StringBuilder text = new();
        private bool omitted;
        internal void Add(string line)
        {
            if (omitted) return;
            if (text.Length + line.Length + Environment.NewLine.Length > MaximumDetailsCharacters - 128) { omitted = true; return; }
            text.AppendLine(line);
        }
        public override string ToString() => text.ToString() + (omitted
            ? "Additional captured model/row details omitted at the display bound; the complete payload was validated.\n" : "");
    }
}
