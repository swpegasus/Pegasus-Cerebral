using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Auditing.Laya;

internal sealed class LayaStockAdvisorModule : IAdvisorModule
{
    public const string AdapterId = "laya.stock.criterion";
    public const string PackageId = "pegasus.laya-auditor";
    public const string RequestSchema = "cerebral.laya.criterion.request.v1";
    public const string ResultSchema = "cerebral.laya.criterion.result.v1";
    private readonly LayaProcessClient client;
    private long completed;
    private LayaRejectionDiagnostic? lastRejection;
    public AdvisorCapability Capability { get; }
    public object? LastResult { get; private set; }
    public long CompletedRequests => Interlocked.Read(ref completed);
    public LayaRejectionDiagnostic? LastRejection => Volatile.Read(ref lastRejection);
    internal LayaRejectionDiagnostic RecordRejection(string requestId, LayaRejectedException rejection)
    {
        var diagnostic = rejection.Diagnostic with { RequestId = requestId };
        Volatile.Write(ref lastRejection, diagnostic);
        return diagnostic;
    }
    public LayaStockAdvisorModule(LayaConfiguration config, LayaProcessClient client)
    {
        this.client = client;
        var ready = client.Readiness ?? throw new InvalidOperationException("Native CPU readiness must precede auditor admission.");
        var resident = Math.Max(ready.PrivateMemoryBytes, ready.ResidentMemoryBytes);
        var peak = Math.Max(ready.PeakPrivateBytes, ready.PeakMemoryBytes);
        int MiB(long bytes) => checked((int)Math.Ceiling(bytes / 1_048_576d));
        Capability = new(AdvisorContract.Capability, AdapterId, LayaConfiguration.PackageVersion, config.Generation, Array.AsReadOnly(new[] { "auditor" }),
            1, 1, "laya.stock.cpu." + ready.ReadinessIdentity, ready.RunnerSha256, ready.RuntimeIdentity,
            "laya.stock." + ready.WeightsSha256, Array.AsReadOnly(new[] { RequestSchema }), Array.AsReadOnly(new[] { ResultSchema }),
            Array.AsReadOnly(config.Purposes.ToArray()), Array.AsReadOnly(new[] { "criterion_status" }), Array.AsReadOnly(new[] { "local.private" }),
            Array.AsReadOnly(new[] { "synchronous" }), new(32_768, 512, 0, 1, 3, 1),
            new(MiB(resident), MiB(Math.Max(0, peak - resident)), (int)Math.Ceiling(ready.LoadMilliseconds), config.EstimatedP95Milliseconds), "compact");
        Capability.Validate();
    }

    public async Task<WorkflowAdvisorPreparedRequest> PrepareAsync(WorkflowAuditRequest workflow, string originalContextSha256, CancellationToken token)
    {
        var hostContext = WorkflowAdvisorAdapter.CreateContextJson(workflow);
        var state = LayaProtocol.Canonical(workflow.Context);
        var id = Guid.NewGuid().ToString("N");
        var prepared = await client.ExchangeAsync(new { protocol = LayaProtocol.Version, kind = "preflight", id, state, hostContext }, root =>
        {
            LayaProtocol.Rejection(root, id);
            LayaProtocol.Fields(root, "protocol", "kind", "id", "readinessIdentity", "stateSha256", "hostContextSha256", "questionSha256",
                "encodedIdsSha256", "contextTokens", "hostContextTokens", "inputTokens", "headTokens", "markerPositions");
            return LayaProtocol.Prepared(root, id, state, hostContext, client.Readiness!);
        }, token).ConfigureAwait(false);
        if (prepared.HostContextTokens > Capability.Limits.MaxContextTokens)
            throw new LayaRejectedException("host-context-token-limit", prepared.ContextTokens, prepared.HostContextTokens, prepared.InputTokens);
        var domain = LayaProtocol.Canonical(new
        {
            schema_version = RequestSchema, original_context_sha256 = originalContextSha256, host_context_sha256 = prepared.HostContextSha256,
            projection_json = state, projection_sha256 = prepared.StateSha256, encoded_ids_sha256 = prepared.EncodedIdsSha256,
            question_sha256 = prepared.QuestionSha256, readiness_identity = prepared.ReadinessIdentity,
            projection_tokens = prepared.ContextTokens, host_context_tokens = prepared.HostContextTokens,
            input_tokens = prepared.InputTokens, head_tokens = prepared.HeadTokens, marker_positions = prepared.MarkerPositions
        });
        var request = new AdvisorRequest(AdvisorContract.Request, workflow.RequestId, workflow.RequestId, 1,
            Capability.ModuleId, Capability.Sha256(), workflow.RunId, workflow.ConversationId, workflow.RunId,
            workflow.ConversationRevision, workflow.RunRevision, prepared.HostContextSha256, prepared.QuestionSha256,
            "workflow.advisory", "local.private", "low", workflow.Purpose, "auditor", "synchronous",
            workflow.CreatedMonotonicMilliseconds, workflow.DeadlineMonotonicMilliseconds,
            workflow.DeadlineMonotonicMilliseconds - workflow.CreatedMonotonicMilliseconds, workflow.RequestId,
            "cerebral.workflow-advisor.context.v1", hostContext, Encoding.UTF8.GetByteCount(hostContext), prepared.HostContextTokens,
            [], [], RequestSchema, domain);
        return new(request, prepared.InputTokens) { InputProjection = new(state, prepared.StateSha256, prepared.ContextTokens) };
    }

    public ValueTask<AdvisorResult> InvokeAsync(AdvisorRequest request, AdvisorHostState hostState, CancellationToken token)
        => InvokeWithDiagnosticsAsync(request, hostState, null, token);
    internal async ValueTask<AdvisorResult> InvokeWithDiagnosticsAsync(AdvisorRequest request, AdvisorHostState hostState,
        Action<LayaRejectionDiagnostic>? rejected, CancellationToken token)
    {
        request.Validate(Capability);
        if (hostState.Cancelled || token.IsCancellationRequested) return Terminal(request, "cancelled", "cancelled");
        if (hostState.CurrentRevision != request.InputRevision || hostState.CurrentContextSha256 != request.ContextSha256 ||
            hostState.CurrentSequence != request.Sequence) return Terminal(request, "stale", "host_state_changed");
        if (Now >= request.DeadlineMonoMs) return Terminal(request, "timed_out", "deadline_reached");
        if (!client.Ready) return Terminal(request, "unavailable", "native_cpu_unavailable");
        using var domain = LayaProtocol.Parse(request.DomainRequestJson);
        var root = domain.RootElement;
        ValidateDomain(request, root);
        var state = root.GetProperty("projection_json").GetString()!;
        var id = Guid.NewGuid().ToString("N");
        LayaPrediction prediction;
        try
        {
            prediction = await client.ExchangeAsync(new
            {
                protocol = LayaProtocol.Version, kind = "predict", id, state, hostContext = request.ContextJson,
                stateSha256 = root.GetProperty("projection_sha256").GetString(), hostContextSha256 = request.ContextSha256,
                questionSha256 = request.DecisionSchemaSha256, encodedIdsSha256 = root.GetProperty("encoded_ids_sha256").GetString()
            }, reply => ReadPrediction(reply, id, state, request.ContextJson, root), token).ConfigureAwait(false);
        }
        catch (LayaRejectedException rejection)
        {
            var diagnostic = RecordRejection(request.RequestId, rejection);
            rejected?.Invoke(diagnostic);
            return Terminal(request, "unavailable", "native_packet_rejected");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return Terminal(request, "timed_out", "native_transport_deadline"); }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
        { return Terminal(request, "rejected", "invalid_native_packet"); }
        catch (IOException) { return Terminal(request, "unavailable", "native_cpu_unavailable"); }
        Interlocked.Increment(ref completed);
        if (token.IsCancellationRequested) return Terminal(request, "cancelled", "late_cancelled_result");
        if (Now >= request.DeadlineMonoMs) return Terminal(request, "timed_out", "late_result");
        var result = LayaProtocol.Canonical(new
        {
            schema_version = ResultSchema, request_id = request.RequestId, host_context_sha256 = request.ContextSha256,
            original_context_sha256 = root.GetProperty("original_context_sha256").GetString(), projection_sha256 = prediction.Preparation.StateSha256,
            encoded_ids_sha256 = prediction.Preparation.EncodedIdsSha256, question_sha256 = prediction.Preparation.QuestionSha256,
            readiness_identity = prediction.Preparation.ReadinessIdentity, option_order = LayaProtocol.Options,
            criterion_status = prediction.Choice, probabilities = prediction.Probabilities, confidence = prediction.Confidence,
            answer_confidence = prediction.AnswerConfidence, action_probability = prediction.ActionProbability,
            calibration = "unvalidated", advisory_only = true, direct_action_allowed = false,
            elapsed_milliseconds = prediction.ElapsedMilliseconds, temperature = prediction.Temperature,
            input_tokens = prediction.Preparation.InputTokens, output_tokens = 0
        });
        LastResult = new { criterion = prediction.Choice, hostContextSha256 = request.ContextSha256,
            originalContextSha256 = root.GetProperty("original_context_sha256").GetString(),
            projectionSha256 = prediction.Preparation.StateSha256, encodedIdsSha256 = prediction.Preparation.EncodedIdsSha256,
            resultSha256 = LayaProtocol.Hash(result), inputTokens = prediction.Preparation.InputTokens, outputTokens = 0,
            elapsedMilliseconds = prediction.ElapsedMilliseconds, calibration = "unvalidated" };
        return new(AdvisorContract.Result, request.RequestId, request.IdempotencyKey, request.Attempt, Capability.ModuleId,
            Capability.InstanceGeneration, request.CapabilitySha256, "accepted", "native_criterion_assessment", ResultSchema,
            result, LayaProtocol.Hash(result), true, false);
    }
    private LayaPrediction ReadPrediction(JsonElement reply, string id, string state, string hostContext, JsonElement domain)
    {
        LayaProtocol.Rejection(reply, id);
        LayaProtocol.Fields(reply, "protocol", "kind", "id", "readinessIdentity", "stateSha256", "hostContextSha256", "questionSha256",
            "encodedIdsSha256", "contextTokens", "hostContextTokens", "inputTokens", "headTokens", "markerPositions", "nativeAnswer",
            "elapsedMilliseconds", "temperature", "outputTokens", "residentMemoryBytes", "peakMemoryBytes", "privateMemoryBytes", "peakPrivateBytes");
        var prepared = LayaProtocol.Prepared(reply, id, state, hostContext, client.Readiness!, "result");
        if (prepared.EncodedIdsSha256 != domain.GetProperty("encoded_ids_sha256").GetString() ||
            prepared.ContextTokens != domain.GetProperty("projection_tokens").GetInt32() ||
            prepared.HostContextTokens != domain.GetProperty("host_context_tokens").GetInt32() ||
            prepared.InputTokens != domain.GetProperty("input_tokens").GetInt32() || prepared.HeadTokens != domain.GetProperty("head_tokens").GetInt32() ||
            !prepared.MarkerPositions.SequenceEqual(domain.GetProperty("marker_positions").EnumerateArray().Select(x => x.GetInt32())) ||
            LayaProtocol.Integer(reply, "outputTokens", 0, 0) != 0)
            throw new InvalidDataException("Laya prediction changed its exact prepared token sequence.");
        var answer = reply.GetProperty("nativeAnswer");
        LayaProtocol.Fields(answer, "choice", "probabilities", "confidence", "answerConfidence", "actionProbability");
        var choice = LayaProtocol.Text(answer, "choice", 32);
        var probabilities = answer.GetProperty("probabilities").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (!LayaProtocol.Options.Contains(choice) || probabilities.Length != 3 ||
            probabilities.Any(x => !double.IsFinite(x) || x is < 0 or > 1) || Math.Abs(probabilities.Sum() - 1) > .001)
            throw new InvalidDataException("Laya native choice probabilities are invalid.");
        var temperature = LayaProtocol.Number(reply, "temperature", .01, 100);
        if (temperature != client.Readiness!.Temperature) throw new InvalidDataException("Laya applied an unbound temperature.");
        foreach (var name in new[] { "residentMemoryBytes", "peakMemoryBytes", "privateMemoryBytes", "peakPrivateBytes" }) _ = LayaProtocol.Integer(reply, name, 1);
        return new(prepared, choice, probabilities, LayaProtocol.Number(answer, "confidence", 0, 1),
            LayaProtocol.Number(answer, "answerConfidence", 0, 1), LayaProtocol.Number(answer, "actionProbability", 0, 1),
            LayaProtocol.Number(reply, "elapsedMilliseconds"), temperature);
    }
    private void ValidateDomain(AdvisorRequest request, JsonElement root)
    {
        LayaProtocol.Fields(root, "schema_version", "original_context_sha256", "host_context_sha256", "projection_json", "projection_sha256",
            "encoded_ids_sha256", "question_sha256", "readiness_identity", "projection_tokens", "host_context_tokens", "input_tokens", "head_tokens", "marker_positions");
        var state = root.GetProperty("projection_json").GetString()!;
        using var hostContext = LayaProtocol.Parse(request.ContextJson);
        if (request.DomainRequestSchemaId != RequestSchema || LayaProtocol.Text(root, "schema_version") != RequestSchema ||
            LayaProtocol.Digest(root, "host_context_sha256") != request.ContextSha256 ||
            LayaProtocol.Digest(root, "projection_sha256") != LayaProtocol.Hash(state) ||
            state != LayaProtocol.Canonical(hostContext.RootElement.GetProperty("packet")) ||
            LayaProtocol.Digest(root, "question_sha256") != request.DecisionSchemaSha256 ||
            request.DecisionSchemaSha256 != client.Readiness!.QuestionSha256 ||
            LayaProtocol.Digest(root, "readiness_identity") != client.Readiness.ReadinessIdentity ||
            LayaProtocol.Integer(root, "host_context_tokens", 1, 512) != request.ContextTokens)
            throw new AuditorContractException("Native Laya domain request changed its sealed projection or host-context binding.");
        _ = LayaProtocol.Digest(root, "original_context_sha256"); _ = LayaProtocol.Digest(root, "encoded_ids_sha256");
        _ = LayaProtocol.Integer(root, "input_tokens", 1, 512); _ = LayaProtocol.Integer(root, "projection_tokens", 1, 512);
        _ = LayaProtocol.Integer(root, "head_tokens", 1, 256);
    }
    public static WorkflowAdvisorCriterion ReadCriterion(AdvisorRequest request, AdvisorResult result)
    {
        using var original = LayaProtocol.Parse(request.DomainRequestJson);
        using var parsed = LayaProtocol.Parse(result.DomainResultJson!);
        var root = parsed.RootElement;
        if (result.DomainResultSchemaId != ResultSchema || LayaProtocol.Text(root, "schema_version") != ResultSchema ||
            LayaProtocol.Text(root, "request_id", 128) != request.RequestId ||
            LayaProtocol.Text(root, "host_context_sha256") != request.ContextSha256 ||
            new[] { "original_context_sha256", "projection_sha256", "encoded_ids_sha256", "question_sha256", "readiness_identity" }
                .Any(name => root.GetProperty(name).GetString() != original.RootElement.GetProperty(name).GetString()) ||
            !root.GetProperty("option_order").EnumerateArray().Select(x => x.GetString()).SequenceEqual(LayaProtocol.Options) ||
            root.GetProperty("advisory_only").ValueKind != JsonValueKind.True || root.GetProperty("direct_action_allowed").ValueKind != JsonValueKind.False ||
            LayaProtocol.Text(root, "calibration") != "unvalidated")
            throw new AuditorContractException("Native Laya result is not bound to this exact advisory request.");
        return LayaProtocol.Text(root, "criterion_status", 32) switch
        { "met" => WorkflowAdvisorCriterion.Met, "gap" => WorkflowAdvisorCriterion.Gap,
            "insufficient_evidence" => WorkflowAdvisorCriterion.InsufficientEvidence,
            _ => throw new AuditorContractException("Native Laya criterion status is invalid.") };
    }
    private AdvisorResult Terminal(AdvisorRequest request, string disposition, string code) =>
        new(AdvisorContract.Result, request.RequestId, request.IdempotencyKey, request.Attempt, Capability.ModuleId,
            Capability.InstanceGeneration, request.CapabilitySha256, disposition, code, null, null, null, true, false);
    internal static long Now => (long)(Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency);
}
