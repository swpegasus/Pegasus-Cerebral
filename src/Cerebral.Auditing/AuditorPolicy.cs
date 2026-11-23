using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace Cerebral.Auditing;

public sealed record AuditorPolicy(string SchemaVersion, IReadOnlyDictionary<string, string> RiskMinimumTier,
    IReadOnlyList<string> SynchronousRisks, IReadOnlyList<string> CrossWorkerRisks,
    IReadOnlyList<string> NoSilentFallbackRisks, int MinimumMemoryHeadroomMiB, int AgentColocationPenaltyMs)
{
    private static readonly string[] Fields = ["schema_version", "risk_minimum_tier", "synchronous_risks", "cross_worker_risks",
        "no_silent_fallback_risks", "minimum_memory_headroom_mib", "agent_colocation_penalty_ms"];

    public static AuditorPolicy Parse(string json)
    {
        var data = AuditorJson.ParseObject(json, "selection policy");
        AuditorJson.Fields(data, "selection policy", Fields);
        if (AuditorJson.Text(data["schema_version"], "schema_version") != AuditorSchema.SelectionPolicy)
            throw new AuditorContractException("selection policy version mismatch.");
        var raw = AuditorJson.Object(data["risk_minimum_tier"], "risk_minimum_tier");
        if (!raw.Select(item => item.Key).ToHashSet(StringComparer.Ordinal).SetEquals(AuditorSchema.RiskClasses))
            throw new AuditorContractException("risk_minimum_tier must cover every risk class exactly.");
        var minimums = raw.ToDictionary(item => item.Key,
            item => AuditorJson.Enum(item.Value, "risk_minimum_tier." + item.Key, AuditorSchema.AssuranceTiers), StringComparer.Ordinal);
        return new(AuditorSchema.SelectionPolicy, new ReadOnlyDictionary<string, string>(minimums),
            AuditorJson.Enums(data["synchronous_risks"], "synchronous_risks", AuditorSchema.RiskClasses),
            AuditorJson.Enums(data["cross_worker_risks"], "cross_worker_risks", AuditorSchema.RiskClasses),
            AuditorJson.Enums(data["no_silent_fallback_risks"], "no_silent_fallback_risks", AuditorSchema.RiskClasses),
            AuditorJson.Integer(data["minimum_memory_headroom_mib"], "minimum_memory_headroom_mib"),
            AuditorJson.Integer(data["agent_colocation_penalty_ms"], "agent_colocation_penalty_ms"));
    }

    public string ToJson()
    {
        var minimums = new JsonObject(); foreach (var item in RiskMinimumTier) minimums[item.Key] = item.Value;
        return AuditorJson.Canonical(new JsonObject
        {
            ["schema_version"] = SchemaVersion, ["risk_minimum_tier"] = minimums,
            ["synchronous_risks"] = Array(SynchronousRisks), ["cross_worker_risks"] = Array(CrossWorkerRisks),
            ["no_silent_fallback_risks"] = Array(NoSilentFallbackRisks),
            ["minimum_memory_headroom_mib"] = MinimumMemoryHeadroomMiB,
            ["agent_colocation_penalty_ms"] = AgentColocationPenaltyMs
        });
    }
    private static JsonArray Array(IEnumerable<string> values) => new(values.Select(value => JsonValue.Create(value)).ToArray<JsonNode?>());
}

public sealed record AuditResult(string SchemaVersion, string AuditId, string AgentTaskId, string AgentWorkerId,
    string? AuditWorkerId, string? BackendId, long InputRevision, string InputSha256, long Sequence, string Status,
    JsonObject? DecisionResponse, string? ErrorCode, long CompletedMonoMs, bool AdvisoryOnly, bool DirectActionAllowed)
{
    private static readonly string[] Fields = ["schema_version", "audit_id", "agent_task_id", "agent_worker_id", "audit_worker_id",
        "backend_id", "input_revision", "input_sha256", "sequence", "status", "decision_response", "error_code",
        "completed_mono_ms", "advisory_only", "direct_action_allowed"];

    public static AuditResult Parse(string json, AuditRequest request)
    {
        var data = AuditorJson.ParseObject(json, "audit result");
        AuditorJson.Fields(data, "audit result", Fields);
        if (AuditorJson.Text(data["schema_version"], "schema_version") != AuditorSchema.AuditResult)
            throw new AuditorContractException("audit result version mismatch.");
        if (!AuditorJson.Boolean(data["advisory_only"], "advisory_only"))
            throw new AuditorContractException("semantic audit results must remain advisory.");
        if (AuditorJson.Boolean(data["direct_action_allowed"], "direct_action_allowed"))
            throw new AuditorContractException("audit results cannot directly authorize actions.");
        var status = AuditorJson.Enum(data["status"], "status", AuditorSchema.ResultStatuses);
        var auditWorker = AuditorJson.OptionalIdentifier(data["audit_worker_id"], "audit_worker_id");
        var backend = AuditorJson.OptionalIdentifier(data["backend_id"], "backend_id");
        JsonObject? response = null;
        string? error = null;
        if (status == "completed")
        {
            if (auditWorker is null || backend is null || data["decision_response"] is not JsonObject responseObject)
                throw new AuditorContractException("completed audit result requires backend identity and response.");
            if (data["error_code"] is not null) throw new AuditorContractException("completed audit result cannot contain error_code.");
            ValidateDecisionResponse(responseObject, request.DecisionRequest);
            response = (JsonObject)AuditorJson.Clone(responseObject);
        }
        else
        {
            if (data["decision_response"] is not null || data["error_code"] is null)
                throw new AuditorContractException("non-completed audit result requires error_code and no response.");
            error = AuditorJson.Identifier(data["error_code"], "error_code");
        }
        var result = new AuditResult(AuditorSchema.AuditResult, AuditorJson.Identifier(data["audit_id"], "audit_id"),
            AuditorJson.Identifier(data["agent_task_id"], "agent_task_id"), AuditorJson.Identifier(data["agent_worker_id"], "agent_worker_id"),
            auditWorker, backend, AuditorJson.Long(data["input_revision"], "input_revision"), AuditorJson.Sha256(data["input_sha256"], "input_sha256"),
            AuditorJson.Long(data["sequence"], "sequence"), status, response, error,
            AuditorJson.Long(data["completed_mono_ms"], "completed_mono_ms"), true, false);
        if (result.AuditId != request.AuditId || result.AgentTaskId != request.AgentTaskId ||
            result.AgentWorkerId != request.AgentWorkerId || result.InputRevision != request.InputRevision ||
            result.InputSha256 != request.InputSha256 || result.Sequence != request.Sequence)
            throw new AuditorContractException("audit result identity does not match request.");
        return result;
    }

    public string ToJson() => AuditorJson.Canonical(new JsonObject
    {
        ["schema_version"] = SchemaVersion, ["audit_id"] = AuditId, ["agent_task_id"] = AgentTaskId,
        ["agent_worker_id"] = AgentWorkerId, ["audit_worker_id"] = AuditWorkerId, ["backend_id"] = BackendId,
        ["input_revision"] = InputRevision, ["input_sha256"] = InputSha256, ["sequence"] = Sequence, ["status"] = Status,
        ["decision_response"] = DecisionResponse is null ? null : AuditorJson.Clone(DecisionResponse), ["error_code"] = ErrorCode,
        ["completed_mono_ms"] = CompletedMonoMs, ["advisory_only"] = AdvisoryOnly,
        ["direct_action_allowed"] = DirectActionAllowed
    });

    private static void ValidateDecisionResponse(JsonObject response, DecisionRequest request)
    {
        AuditorJson.Fields(response, "decision response", ["schema_version", "request_id", "mode", "results"]);
        if (AuditorJson.Text(response["schema_version"], "response.schema_version") != request.SchemaVersion ||
            AuditorJson.Text(response["request_id"], "response.request_id") != request.RequestId)
            throw new AuditorContractException("response identity does not match request.");
        _ = AuditorJson.Enum(response["mode"], "response.mode",
            ["direct", "direct_batched", "shared_serial", "shared_parallel", "generate"]);
        var results = AuditorJson.Array(response["results"], "response.results");
        if (results.Count != request.Questions.Count) throw new AuditorContractException("response must contain one result per question.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in results)
        {
            var item = AuditorJson.Object(node, "question result");
            AuditorJson.Fields(item, "question result", ["question_id", "status", "predicted_option_id", "accepted_option_id",
                "candidate_logits", "uncalibrated_probabilities", "error"]);
            var questionId = AuditorJson.Identifier(item["question_id"], "question_id");
            if (!seen.Add(questionId)) throw new AuditorContractException("duplicate response question id.");
            var question = request.Questions.SingleOrDefault(value => value.Id == questionId) ??
                           throw new AuditorContractException("response question coverage mismatch.");
            var status = AuditorJson.Enum(item["status"], "result.status", ["ok", "abstained", "error"]);
            var predicted = AuditorJson.OptionalIdentifier(item["predicted_option_id"], "predicted_option_id");
            var accepted = AuditorJson.OptionalIdentifier(item["accepted_option_id"], "accepted_option_id");
            var error = AuditorJson.OptionalText(item["error"], "error", 8_000);
            var allowed = question.Options.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
            if (predicted is not null && !allowed.Contains(predicted) || accepted is not null && !allowed.Contains(accepted))
                throw new AuditorContractException("response option is not declared.");
            if (status == "ok" && accepted is null || status != "ok" && accepted is not null)
                throw new AuditorContractException("accepted option does not match result status.");
            if (status == "error" && error is null) throw new AuditorContractException("error result requires an error message.");
            ValidateScores(item["candidate_logits"], "candidate_logits", allowed, false);
            ValidateScores(item["uncalibrated_probabilities"], "uncalibrated_probabilities", allowed, true);
        }
        if (!seen.SetEquals(request.Questions.Select(item => item.Id)))
            throw new AuditorContractException("response question coverage mismatch.");
    }

    private static void ValidateScores(JsonNode? node, string name, HashSet<string> allowed, bool probabilities)
    {
        var map = AuditorJson.Object(node, name);
        if (!map.Select(item => item.Key).ToHashSet(StringComparer.Ordinal).SetEquals(allowed))
            throw new AuditorContractException(name + " must cover every option exactly.");
        var values = map.Select(item => AuditorJson.Number(item.Value, name + " value")).ToArray();
        if (probabilities && (values.Any(value => value < 0) || Math.Abs(values.Sum() - 1.0) > 1e-6))
            throw new AuditorContractException("probabilities do not sum to one within tolerance.");
    }
}

public sealed record DeliveryDecision(string Status, string ReasonCode);

public static class AuditDelivery
{
    public static DeliveryDecision Resolve(AuditRequest request, AuditResult result, long latestRevision,
        string latestInputSha256, long latestSequence, long nowMonoMs)
    {
        request = AuditRequest.Parse(request.ToJson());
        result = AuditResult.Parse(result.ToJson(), request);
        if (latestRevision < 0 || latestSequence < 0 || nowMonoMs < 0)
            throw new AuditorContractException("delivery counters must be nonnegative.");
        _ = AuditorJson.Sha256(JsonValue.Create(latestInputSha256), "latest_input_sha256");
        if (latestRevision != request.InputRevision || latestInputSha256 != request.InputSha256 || latestSequence != request.Sequence)
            return new("stale", "superseded_input");
        if (result.CompletedMonoMs > nowMonoMs) return new("failed", "completion_time_in_future");
        if (result.CompletedMonoMs > request.DeadlineMonoMs || nowMonoMs > request.DeadlineMonoMs)
            return new("timed_out", "deadline_exceeded");
        if (nowMonoMs - result.CompletedMonoMs > request.MaxResultAgeMs) return new("stale", "result_age_exceeded");
        return result.Status == "completed" ? new("accepted", "current_revision") : new(result.Status, result.ErrorCode ?? result.Status);
    }
}
