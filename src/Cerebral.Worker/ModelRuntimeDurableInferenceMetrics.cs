using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Worker;

[JsonConverter(typeof(JsonStringEnumConverter<DurableMetricAvailability>))]
internal enum DurableMetricAvailability { Reported, NotReported, NotRecorded, Invalid }

internal sealed record DurableMetricNumber(long? Value, DurableMetricAvailability Availability);
internal sealed record DurableInferenceScope(string RunId, string RequestId, string ConversationId, string? ProjectId);
internal sealed record DurableInferenceWindow(long AfterSequence, long SnapshotSequence, bool Complete, bool RetentionGap);
internal sealed record DurablePreparedMetrics(string? ModelId, string? InstanceId, string? AdapterId, string? AdapterVersion,
    DurableMetricNumber InputTokens, DurableMetricNumber EffectiveContext, DurableMetricNumber ResponseBudget,
    DurableMetricNumber DesiredResponseBudget, DurableMetricNumber ReasoningReserveTokens,
    DurableMetricNumber SafetyReserveTokens, string CountBasis);
internal sealed record DurableInferenceOrigin(string Value, string Basis, string? WorkerId = null,
    string? WorkerInstallationId = null);
internal sealed record DurableInferenceEffect(string EffectId, string CommandId, int CommandVersion,
    string IntentEventId, long IntentSequence, string? ResultEventId, long? ResultSequence,
    DateTimeOffset? ResultCreatedAt, WorkflowEffectState? ResultState, string MeasurementAvailability,
    DurableMetricNumber HostCommandDurationMilliseconds, int SupersededResults);
internal sealed record DurableInferenceMeasurement(string EffectId, string Kind, int Pass,
    string IntentEventId, long IntentSequence, string ResultEventId, long ResultSequence,
    WorkflowEffectState ResultState, DurablePreparedMetrics Prepared, string Finish,
    DurableMetricNumber ProviderEvaluatedTokens, DurableMetricNumber ProviderGeneratedTokens,
    DurableMetricNumber HostPipelineElapsedMilliseconds, DurableMetricNumber FirstVisibleFragmentOffsetMilliseconds,
    bool? CancellationConfirmed, bool? ProviderErrorPresent, DurableInferenceOrigin Origin)
{
    public string GeneratedTokenScope => "provider-unspecified";
}
internal sealed record DurableInferenceProjection(string Schema, DurableInferenceScope Scope, long RunRevision,
    DurableInferenceWindow Window, bool Complete, bool Truncated, int ExaminedEvents, int IgnoredEvents,
    int RejectedEvents, int DuplicateEvents, int UnresolvedEffects, int OmittedMeasurements, bool IntegrityConflict,
    IReadOnlyList<DurableInferenceEffect> Effects, IReadOnlyList<DurableInferenceMeasurement> Measurements)
{
    public string Provenance => "trusted-owner-journal-and-host-frozen-submission; structural-join-not-authorization";
    public string TimingSemantics => "Host pipeline elapsed includes request/context/preparation, excludes earlier readiness and later disposal; first visible fragment is the first nonempty visible callback on that clock, not provider TTFT.";
    public IReadOnlyList<string> UnavailableDimensions => ["provider-phase-tps", "separate-preparation-duration",
        "lease-identity", "native-slot-identity", "gpu-device"];
}

/// <summary>
/// Pure, Worker-private projection of an already authorized owner's bounded journal snapshot.
/// This is not an event ingestion, authorization, journal read, model execution or telemetry capture API.
/// Only value DTOs leave this method; event/result JSON, prompts, output, profiles and errors stay local.
/// </summary>
internal static class ModelRuntimeDurableInferenceMetrics
{
    // Diagnostic window/output bounds only. They impose no workflow, retry or inference-capacity ceiling.
    internal const int MaximumInputEvents = 4096;
    internal const int MaximumMeasurements = 128;
    private const string ResultSchema = "workflow.command.result";
    private const string NativeCountBasis =
        "Exact llama.cpp template token IDs (add_special=true, parse_special=true); identical IDs sent to completion.";
    private static readonly DurableMetricNumber Missing = new(null, DurableMetricAvailability.NotRecorded);
    private static readonly DurableMetricNumber Invalid = new(null, DurableMetricAvailability.Invalid);
    private sealed record Intent(CerebralEvent Event, string Command, int Version);
    private sealed record Candidate(CerebralEvent Event, string Command);

    internal static DurableInferenceProjection Project(DurableInferenceScope scope, WorkflowRun run,
        IReadOnlyList<CerebralEvent> events, DurableInferenceWindow window, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(window);
        if (!Identifier(scope.RunId) || !Identifier(scope.RequestId) || !Identifier(scope.ConversationId) ||
            scope.ProjectId is not null && !Identifier(scope.ProjectId) ||
            run.Submission is null || scope.RunId != run.Id || scope.RequestId != run.Submission.RequestId ||
            scope.ConversationId != run.Submission.ConversationId || scope.ProjectId != run.Submission.ProjectId)
            throw new ArgumentException("Durable inference scope does not match the host-frozen run.");
        if (events.Count > MaximumInputEvents || window.AfterSequence < run.Submission.ConversationSequence ||
            window.SnapshotSequence < window.AfterSequence)
            throw new ArgumentException("Durable inference window is invalid or exceeds its diagnostic bound.");

        var intents = new Dictionary<string, Intent>(StringComparer.Ordinal);
        var results = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var resultCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        var ids = new Dictionary<string, (long Sequence, string Digest)>(StringComparer.Ordinal);
        var sequences = new Dictionary<long, string>();
        int ignored = 0, rejected = 0, duplicates = 0, unresolved = 0, omitted = 0;
        bool conflict = false;
        foreach (var item in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is null) { rejected++; continue; }
            if (item.ConversationId != scope.ConversationId || item.TurnId != scope.RunId)
            { ignored++; continue; }
            if (item.Provenance?.JobId != scope.RunId || item.Sequence <= window.AfterSequence ||
                item.Sequence > window.SnapshotSequence) { rejected++; continue; }
            try { item.Validate(); }
            catch (Exception error) when (error is ArgumentException or JsonException or NotSupportedException)
            { rejected++; continue; }
            // Detect overlapping pages without counting the same persisted event twice. Conflicting
            // journal coordinates invalidate the whole view rather than selecting an arbitrary body.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(item, CerebralJson.Options);
            if (bytes.Length > CerebralEventContract.MaximumSerializedUtf8Bytes) { rejected++; continue; }
            var digest = Convert.ToHexString(SHA256.HashData(bytes));
            Array.Clear(bytes);
            if (ids.TryGetValue(item.Id, out var old))
            {
                if (old.Sequence == item.Sequence && old.Digest == digest) duplicates++;
                else conflict = true;
                continue;
            }
            if (sequences.TryGetValue(item.Sequence, out var prior) && prior != item.Id)
            { conflict = true; continue; }
            ids.Add(item.Id, (item.Sequence, digest)); sequences[item.Sequence] = item.Id;
            if (item.Type == CerebralEventTypes.ToolResult)
            {
                // Correlation and the host-owned actor/source identify a newer result even if its
                // detail payload is damaged. Preserve that supersession; TryResult will refuse it.
                var command = item.Actor.Id;
                if (command is not (WorkerWorkflowEffects.GenerateCommand or WorkerWorkflowEffects.CompactCommand))
                { ignored++; continue; }
                if (item.Actor.Kind != EventActorKind.Tool || item.Provenance.Source != EventSourceKind.Tool ||
                    item.Provenance.SourceId != command || !Identifier(item.CorrelationId))
                { rejected++; continue; }
                var effect = item.CorrelationId!;
                resultCounts[effect] = resultCounts.GetValueOrDefault(effect) + 1;
                if (!results.TryGetValue(effect, out var latest) || item.Sequence > latest.Event.Sequence)
                    results[effect] = new(item, command);
                continue;
            }
            if (item.Type != CerebralEventTypes.ToolCall) { ignored++; continue; }
            if (item.Data is not { ValueKind: JsonValueKind.Object } data)
            { rejected++; continue; }
            var intentEffect = Text(data, "callId");
            var intentCommand = Text(data, "toolName", 256);
            if (intentCommand is not (WorkerWorkflowEffects.GenerateCommand or WorkerWorkflowEffects.CompactCommand))
            { ignored++; continue; }
            if (intentEffect is null || intentEffect != item.CorrelationId) { rejected++; continue; }
            if (item.Actor.Kind != EventActorKind.Agent || item.Actor.Id != run.Submission.ProfileId ||
                item.Provenance.Source != EventSourceKind.Service || item.Provenance.SourceId != "cerebral.engine")
            { rejected++; continue; }
            if (!UniqueFields(data)) { blocked.Add(intentEffect); rejected++; continue; }
            var version = Number(data, "commandVersion");
            if (version.Availability != DurableMetricAvailability.Reported ||
                intentCommand == WorkerWorkflowEffects.GenerateCommand && version.Value is not (1 or 2) ||
                intentCommand == WorkerWorkflowEffects.CompactCommand && version.Value != 1)
            { blocked.Add(intentEffect); rejected++; continue; }
            if (!intents.TryAdd(intentEffect, new(item, intentCommand, (int)version.Value!.Value)))
            { blocked.Add(intentEffect); rejected++; }
        }

        var effects = new List<DurableInferenceEffect>();
        var measurements = new List<DurableInferenceMeasurement>();
        if (!conflict)
        {
            foreach (var pair in intents.OrderBy(x => x.Value.Event.Sequence))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var effect = pair.Key; var intent = pair.Value;
                if (blocked.Contains(effect)) { unresolved++; continue; }
                if (!results.TryGetValue(effect, out var candidate))
                {
                    unresolved++;
                    effects.Add(new(effect, intent.Command, intent.Version, intent.Event.Id, intent.Event.Sequence,
                        null, null, null, null, "not-recorded", Missing, 0));
                    continue;
                }
                var item = candidate.Event;
                if (candidate.Command != intent.Command || item.Sequence <= intent.Event.Sequence)
                { rejected++; unresolved++; continue; }
                var superseded = Math.Max(0, resultCounts.GetValueOrDefault(effect) - 1);
                if (!TryResult(item, out var result, out var duration))
                {
                    rejected++; unresolved++;
                    effects.Add(new(effect, intent.Command, intent.Version, intent.Event.Id, intent.Event.Sequence,
                        item.Id, item.Sequence, item.CreatedAt, null, "invalid-latest-result", duration, superseded));
                    continue;
                }
                var body = result!.Data ?? default;
                var availability = "not-recorded";
                if (intent.Command == WorkerWorkflowEffects.GenerateCommand)
                {
                    var generation = Field(body, "generation", out var malformed);
                    if (!malformed && generation.ValueKind == JsonValueKind.Object)
                    {
                        availability = "recorded";
                        Add("generation", 1, body, generation);
                    }
                    else if (malformed || generation.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                        availability = "invalid";
                }
                else
                {
                    var usage = Field(body, "usage", out var malformed);
                    if (!malformed && usage.ValueKind == JsonValueKind.Array)
                    {
                        availability = "recorded";
                        var pass = 0;
                        foreach (var part in usage.EnumerateArray())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            pass++;
                            if (part.ValueKind != JsonValueKind.Object)
                            { rejected++; availability = "invalid"; continue; }
                            Add("compaction", pass, part, part);
                        }
                    }
                    else if (malformed || usage.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                        availability = "invalid";
                }
                if (availability == "invalid") { rejected++; unresolved++; }
                effects.Add(new(effect, intent.Command, intent.Version, intent.Event.Id, intent.Event.Sequence,
                    item.Id, item.Sequence, item.CreatedAt, result.State, availability, duration, superseded));

                void Add(string kind, int pass, JsonElement preparationContainer, JsonElement terminal)
                {
                    if (measurements.Count == MaximumMeasurements) { omitted++; return; }
                    var prepared = Prepare(Field(preparationContainer, "prepared", out var badPrepared), badPrepared);
                    measurements.Add(new(effect, kind, pass, intent.Event.Id, intent.Event.Sequence,
                        item.Id, item.Sequence, result.State, prepared, Finish(terminal),
                        Number(terminal, "evaluatedTokens"), Number(terminal, "generatedTokens"),
                        kind == "generation" ? Number(body, "elapsedMilliseconds") : Missing,
                        kind == "generation" ? Number(body, "firstTokenMilliseconds") : Missing,
                        kind == "generation" ? Boolean(terminal, "cancellationConfirmed") : null,
                        kind == "generation" ? ErrorPresent(terminal) : null,
                        Origin(run.Submission.SettingsSnapshot, prepared, kind, intent.Version)));
                }
            }
            unresolved += results.Keys.Count(effect => !intents.ContainsKey(effect));
        }
        var complete = window.Complete && !window.RetentionGap &&
            window.AfterSequence == run.Submission.ConversationSequence && rejected == 0 &&
            unresolved == 0 && omitted == 0 && !conflict;
        return new("cerebral.durable-inference-projection/1", scope, run.Revision, window, complete,
            omitted != 0, events.Count, ignored, rejected, duplicates, unresolved, omitted, conflict,
            effects.AsReadOnly(), measurements.AsReadOnly());
    }

    private static bool TryResult(CerebralEvent item, out WorkflowCommandResult? result,
        out DurableMetricNumber duration)
    {
        result = null; duration = Missing;
        try
        {
            if (item.Data is not { ValueKind: JsonValueKind.Object } data) return false;
            duration = Number(data, "durationMilliseconds");
            if (!UniqueFields(data) || Boolean(data, "success") is not { } success || item.Content.Count != 1 ||
                item.Content[0] is not StructuredContentPart { Schema: ResultSchema } part ||
                part.Value.ValueKind != JsonValueKind.Object || !UniqueFields(part.Value) ||
                Field(part.Value, "state", out _).ValueKind == JsonValueKind.Undefined) return false;
            _ = Field(part.Value, "data", out var badData);
            if (badData) return false;
            var details = data.Deserialize<ToolResultData>(CerebralJson.Options);
            result = part.Value.Deserialize<WorkflowCommandResult>(CerebralJson.Options);
            return details is not null && details.CallId == item.CorrelationId && details.ToolName == item.Actor.Id &&
                !details.Truncated && result is not null && Enum.IsDefined(result.State) &&
                success == (result.State == WorkflowEffectState.Succeeded) &&
                result.State is WorkflowEffectState.Succeeded or WorkflowEffectState.Failed or
                    WorkflowEffectState.Cancelled or WorkflowEffectState.Unknown;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        { result = null; return false; }
    }

    private static DurablePreparedMetrics Prepare(JsonElement prepared, bool invalidParent) => invalidParent
        ? new(null, null, null, null, Invalid, Invalid, Invalid, Invalid, Invalid, Invalid, "invalid")
        : new(Text(prepared, "modelId"), Text(prepared, "instanceId"), Text(prepared, "adapterId"),
            Text(prepared, "adapterVersion"), Number(prepared, "inputTokens"), Number(prepared, "effectiveContext"),
            Number(prepared, "responseBudget"), Number(prepared, "desiredResponseBudget"),
            Number(prepared, "reasoningReserveTokens"), Number(prepared, "safetyReserveTokens"),
            Field(prepared, "countBasis", out var bad) is var basis && !bad && basis.ValueKind == JsonValueKind.String
                ? basis.GetString() == NativeCountBasis ? "exact-native-template-token-ids" : "provider-declared-unclassified"
                : bad ? "invalid" : "not-recorded");

    private static DurableInferenceOrigin Origin(JsonElement settings, DurablePreparedMetrics prepared,
        string kind, int version)
    {
        if (kind == "generation" && version == 2)
            return new("unavailable", "selected-policy-not-retained-in-v2-receipt");
        var route = kind == "compaction" ? Field(settings, "compactor", out _) : settings;
        var model = Field(route, "model", out _); var profile = Field(route, "profile", out _);
        var worker = Text(model, "modelWorkerId"); var installation = Text(model, "modelWorkerInstallationId");
        if (prepared.ModelId is not null && prepared.ModelId == Text(model, "id") &&
            worker is not null && worker == Text(profile, "modelWorkerId"))
        {
            if (worker == "local") return new("local-route", "frozen-route-joined-to-prepared-model", worker);
            if (installation is not null)
                return new("remote-route", "frozen-installation-route-joined-to-prepared-model", worker, installation);
        }
        return new("unavailable", "exact-frozen-route-join-not-established");
    }

    private static DurableMetricNumber Number(JsonElement value, string name)
    {
        var field = Field(value, name, out var invalid);
        if (invalid) return Invalid;
        if (field.ValueKind == JsonValueKind.Undefined) return Missing;
        if (field.ValueKind == JsonValueKind.Null) return new(null, DurableMetricAvailability.NotReported);
        return field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var number) && number >= 0
            ? new(number, DurableMetricAvailability.Reported) : Invalid;
    }
    private static string? Text(JsonElement value, string name, int maximum = 128)
    {
        var field = Field(value, name, out var invalid);
        return !invalid && field.ValueKind == JsonValueKind.String && field.GetString() is { } text &&
            Identifier(text, maximum) ? text : null;
    }
    private static bool? Boolean(JsonElement value, string name)
    {
        var field = Field(value, name, out var invalid);
        return invalid ? null : field.ValueKind switch
        { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }
    private static bool? ErrorPresent(JsonElement value)
    {
        var field = Field(value, "error", out var invalid);
        return invalid || field.ValueKind == JsonValueKind.Undefined ? null : field.ValueKind != JsonValueKind.Null;
    }
    private static string Finish(JsonElement value) => Text(value, "finish")?.ToLowerInvariant() switch
    { "stop" => "stop", "length" => "length", "cancelled" => "cancelled", "unknown" => "unknown", _ => "unavailable" };
    private static bool Identifier(string? value, int maximum = 128) =>
        value is { Length: > 0 } && value.Length <= maximum && !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl);
    private static JsonElement Field(JsonElement value, string name, out bool invalid)
    {
        invalid = false;
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return default;
        if (value.ValueKind != JsonValueKind.Object) { invalid = true; return default; }
        JsonElement result = default; var found = false;
        foreach (var property in value.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found || property.Name != name) { invalid = true; return default; }
            found = true; result = property.Value;
        }
        return result;
    }
    private static bool UniqueFields(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return value.ValueKind == JsonValueKind.Object &&
            value.EnumerateObject().All(property => names.Add(property.Name));
    }
}
