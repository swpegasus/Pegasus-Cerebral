using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace Cerebral.Auditing;

public static class AuditorSchema
{
    public const string AuditRequest = "cerebral.audit.request.v1";
    public const string AuditResult = "cerebral.audit.result.v1";
    public const string WorkerSnapshot = "cerebral.worker.snapshot.v1";
    public const string BackendSnapshot = "cerebral.audit.backend.snapshot.v1";
    public const string SelectionPolicy = "cerebral.audit.selection-policy.v1";
    public static readonly string[] RiskClasses = ["low", "medium", "high", "critical"];
    public static readonly string[] AssuranceTiers = ["compact", "standard", "robust"];
    public static readonly string[] ExecutionModes = ["asynchronous", "synchronous"];
    public static readonly string[] AuditKinds =
        ["evidence_sufficiency", "contradiction", "unsafe_action_risk", "stale_context", "policy_mismatch", "escalation_need"];
    public static readonly string[] ResultStatuses = ["completed", "unavailable", "failed", "timed_out", "cancelled", "stale"];
}

public sealed record WorkerSnapshot(string SchemaVersion, string WorkerId, bool Enabled, string HardwareClass,
    string Location, IReadOnlyList<string> DataZones, int MemoryTotalMiB, int MemoryAvailableMiB,
    int ReservedAgentMemoryMiB, int AuditSlots, int ActiveAudits, IReadOnlyList<string> InstalledBackendIds,
    IReadOnlyList<string> ResidentBackendIds, IReadOnlyList<string> ActiveModelIdentities)
{
    private static readonly string[] Fields = ["schema_version", "worker_id", "enabled", "hardware_class", "location",
        "data_zones", "memory_total_mib", "memory_available_mib", "reserved_agent_memory_mib", "audit_slots",
        "active_audits", "installed_backend_ids", "resident_backend_ids", "active_model_identities"];

    public static WorkerSnapshot Parse(string json) => Parse(AuditorJson.ParseObject(json, "worker snapshot"));
    internal static WorkerSnapshot Parse(JsonObject data)
    {
        AuditorJson.Fields(data, "worker snapshot", Fields);
        if (AuditorJson.Text(data["schema_version"], "schema_version") != AuditorSchema.WorkerSnapshot)
            throw new AuditorContractException("worker snapshot version mismatch.");
        var total = AuditorJson.Integer(data["memory_total_mib"], "memory_total_mib", 1);
        var available = AuditorJson.Integer(data["memory_available_mib"], "memory_available_mib");
        var reserved = AuditorJson.Integer(data["reserved_agent_memory_mib"], "reserved_agent_memory_mib");
        var slots = AuditorJson.Integer(data["audit_slots"], "audit_slots", 1);
        var active = AuditorJson.Integer(data["active_audits"], "active_audits");
        var installed = AuditorJson.Identifiers(data["installed_backend_ids"], "installed_backend_ids", true);
        var resident = AuditorJson.Identifiers(data["resident_backend_ids"], "resident_backend_ids", true);
        if (available > total || reserved > total) throw new AuditorContractException("worker memory values exceed memory_total_mib.");
        if (active > slots) throw new AuditorContractException("active_audits exceeds audit_slots.");
        if (resident.Except(installed, StringComparer.Ordinal).Any())
            throw new AuditorContractException("resident backend is not installed on worker.");
        return new(AuditorSchema.WorkerSnapshot, AuditorJson.Identifier(data["worker_id"], "worker_id"),
            AuditorJson.Boolean(data["enabled"], "enabled"), AuditorJson.Text(data["hardware_class"], "hardware_class", 64),
            AuditorJson.Text(data["location"], "location", 128), AuditorJson.Identifiers(data["data_zones"], "data_zones"),
            total, available, reserved, slots, active, installed, resident,
            AuditorJson.Texts(data["active_model_identities"], "active_model_identities", true));
    }

    public string ToJson() => AuditorJson.Canonical(ToNode());
    internal JsonObject ToNode() => new()
    {
        ["schema_version"] = SchemaVersion, ["worker_id"] = WorkerId, ["enabled"] = Enabled,
        ["hardware_class"] = HardwareClass, ["location"] = Location, ["data_zones"] = Array(DataZones),
        ["memory_total_mib"] = MemoryTotalMiB, ["memory_available_mib"] = MemoryAvailableMiB,
        ["reserved_agent_memory_mib"] = ReservedAgentMemoryMiB, ["audit_slots"] = AuditSlots,
        ["active_audits"] = ActiveAudits, ["installed_backend_ids"] = Array(InstalledBackendIds),
        ["resident_backend_ids"] = Array(ResidentBackendIds), ["active_model_identities"] = Array(ActiveModelIdentities)
    };
    private static JsonArray Array(IEnumerable<string> values) => new(values.Select(value => JsonValue.Create(value)).ToArray<JsonNode?>());
}

public sealed record AuditBackendSnapshot(string SchemaVersion, string BackendId, string HostWorkerId, bool Enabled,
    string AssuranceTier, string ModelIdentity, string RuntimeIdentity, int ResidentMemoryMiB, int WorkingMemoryMiB,
    string? SharedBackboneModelIdentity, int SharedBackboneIncrementalMiB, int MaxInputTokens, int MaxOptions,
    IReadOnlyList<string> AuditKinds, IReadOnlyList<string> DataZones, IReadOnlyList<string> ExecutionModes,
    int P95LatencyMs, int ColdStartMs, int QueueCapacity, int QueueDepth, string? CalibrationScopeId)
{
    private static readonly string[] Fields = ["schema_version", "backend_id", "host_worker_id", "enabled", "assurance_tier",
        "model_identity", "runtime_identity", "resident_memory_mib", "working_memory_mib", "shared_backbone_model_identity",
        "shared_backbone_incremental_mib", "max_input_tokens", "max_options", "audit_kinds", "data_zones", "execution_modes",
        "p95_latency_ms", "cold_start_ms", "queue_capacity", "queue_depth", "calibration_scope_id"];

    public static AuditBackendSnapshot Parse(string json) => Parse(AuditorJson.ParseObject(json, "audit backend snapshot"));
    internal static AuditBackendSnapshot Parse(JsonObject data)
    {
        AuditorJson.Fields(data, "audit backend snapshot", Fields);
        if (AuditorJson.Text(data["schema_version"], "schema_version") != AuditorSchema.BackendSnapshot)
            throw new AuditorContractException("audit backend snapshot version mismatch.");
        var capacity = AuditorJson.Integer(data["queue_capacity"], "queue_capacity", 1);
        var depth = AuditorJson.Integer(data["queue_depth"], "queue_depth");
        if (depth > capacity) throw new AuditorContractException("queue_depth exceeds queue_capacity.");
        var shared = AuditorJson.OptionalText(data["shared_backbone_model_identity"], "shared_backbone_model_identity", 256);
        var incremental = AuditorJson.Integer(data["shared_backbone_incremental_mib"], "shared_backbone_incremental_mib");
        if (shared is null && incremental != 0)
            throw new AuditorContractException("shared-backbone memory requires a shared backbone identity.");
        return new(AuditorSchema.BackendSnapshot, AuditorJson.Identifier(data["backend_id"], "backend_id"),
            AuditorJson.Identifier(data["host_worker_id"], "host_worker_id"), AuditorJson.Boolean(data["enabled"], "enabled"),
            AuditorJson.Enum(data["assurance_tier"], "assurance_tier", AuditorSchema.AssuranceTiers),
            AuditorJson.Text(data["model_identity"], "model_identity", 256), AuditorJson.Text(data["runtime_identity"], "runtime_identity", 256),
            AuditorJson.Integer(data["resident_memory_mib"], "resident_memory_mib"), AuditorJson.Integer(data["working_memory_mib"], "working_memory_mib"),
            shared, incremental, AuditorJson.Integer(data["max_input_tokens"], "max_input_tokens", 1),
            AuditorJson.Integer(data["max_options"], "max_options", 2), AuditorJson.Enums(data["audit_kinds"], "audit_kinds", AuditorSchema.AuditKinds),
            AuditorJson.Identifiers(data["data_zones"], "data_zones"), AuditorJson.Enums(data["execution_modes"], "execution_modes", AuditorSchema.ExecutionModes),
            AuditorJson.Integer(data["p95_latency_ms"], "p95_latency_ms", 1), AuditorJson.Integer(data["cold_start_ms"], "cold_start_ms"),
            capacity, depth, AuditorJson.OptionalIdentifier(data["calibration_scope_id"], "calibration_scope_id"));
    }

    public string ToJson() => AuditorJson.Canonical(ToNode());
    internal JsonObject ToNode() => new()
    {
        ["schema_version"] = SchemaVersion, ["backend_id"] = BackendId, ["host_worker_id"] = HostWorkerId, ["enabled"] = Enabled,
        ["assurance_tier"] = AssuranceTier, ["model_identity"] = ModelIdentity, ["runtime_identity"] = RuntimeIdentity,
        ["resident_memory_mib"] = ResidentMemoryMiB, ["working_memory_mib"] = WorkingMemoryMiB,
        ["shared_backbone_model_identity"] = SharedBackboneModelIdentity, ["shared_backbone_incremental_mib"] = SharedBackboneIncrementalMiB,
        ["max_input_tokens"] = MaxInputTokens, ["max_options"] = MaxOptions, ["audit_kinds"] = Array(AuditKinds),
        ["data_zones"] = Array(DataZones), ["execution_modes"] = Array(ExecutionModes), ["p95_latency_ms"] = P95LatencyMs,
        ["cold_start_ms"] = ColdStartMs, ["queue_capacity"] = QueueCapacity, ["queue_depth"] = QueueDepth,
        ["calibration_scope_id"] = CalibrationScopeId
    };
    private static JsonArray Array(IEnumerable<string> values) => new(values.Select(value => JsonValue.Create(value)).ToArray<JsonNode?>());
}

public sealed record AuditRequest(string SchemaVersion, string AuditId, string AgentTaskId, string AgentWorkerId,
    string StreamId, long Sequence, long InputRevision, string InputSha256, string QuestionSchemaSha256,
    string RiskClass, string MinimumTier, string ExecutionMode, long DeadlineMonoMs, long MaxResultAgeMs,
    string DataZone, bool AllowCrossWorker, int EstimatedInputTokens, IReadOnlyDictionary<string, string> CheckKinds,
    IReadOnlyList<string> RequiredPolicyIds, DecisionRequest DecisionRequest)
{
    private static readonly string[] Fields = ["schema_version", "audit_id", "agent_task_id", "agent_worker_id", "stream_id",
        "sequence", "input_revision", "input_sha256", "question_schema_sha256", "risk_class", "minimum_tier", "execution_mode",
        "deadline_mono_ms", "max_result_age_ms", "data_zone", "allow_cross_worker", "estimated_input_tokens", "check_kinds",
        "required_policy_ids", "decision_request"];

    public static AuditRequest Create(DecisionRequest decisionRequest, string auditId, string agentTaskId, string agentWorkerId,
        string streamId, long sequence, long inputRevision, string riskClass, string minimumTier, string executionMode,
        long deadlineMonoMs, long maxResultAgeMs, string dataZone, bool allowCrossWorker, int estimatedInputTokens,
        IReadOnlyDictionary<string, string> checkKinds, IReadOnlyList<string>? requiredPolicyIds = null)
    {
        var node = new JsonObject
        {
            ["schema_version"] = AuditorSchema.AuditRequest, ["audit_id"] = auditId, ["agent_task_id"] = agentTaskId,
            ["agent_worker_id"] = agentWorkerId, ["stream_id"] = streamId, ["sequence"] = sequence,
            ["input_revision"] = inputRevision, ["input_sha256"] = AuditorJson.Hash(JsonValue.Create(decisionRequest.State)!),
            ["question_schema_sha256"] = AuditorJson.Hash(decisionRequest.QuestionSchemaNode()), ["risk_class"] = riskClass,
            ["minimum_tier"] = minimumTier, ["execution_mode"] = executionMode, ["deadline_mono_ms"] = deadlineMonoMs,
            ["max_result_age_ms"] = maxResultAgeMs, ["data_zone"] = dataZone, ["allow_cross_worker"] = allowCrossWorker,
            ["estimated_input_tokens"] = estimatedInputTokens, ["check_kinds"] = Map(checkKinds),
            ["required_policy_ids"] = Array(requiredPolicyIds ?? []), ["decision_request"] = decisionRequest.ToNode()
        };
        return Parse(node);
    }

    public static AuditRequest Parse(string json) => Parse(AuditorJson.ParseObject(json, "audit request"));
    internal static AuditRequest Parse(JsonObject data)
    {
        AuditorJson.Fields(data, "audit request", Fields);
        if (AuditorJson.Text(data["schema_version"], "schema_version") != AuditorSchema.AuditRequest)
            throw new AuditorContractException("audit request version mismatch.");
        var decision = DecisionRequest.Parse(data["decision_request"]);
        var kindsObject = AuditorJson.Object(data["check_kinds"], "check_kinds");
        var kinds = kindsObject.ToDictionary(item => AuditorJson.Identifier(JsonValue.Create(item.Key), "check_kinds key"),
            item => AuditorJson.Enum(item.Value, "check kind", AuditorSchema.AuditKinds), StringComparer.Ordinal);
        if (!kinds.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(decision.Questions.Select(item => item.Id)))
            throw new AuditorContractException("check_kinds must cover every decision question exactly.");
        var inputHash = AuditorJson.Sha256(data["input_sha256"], "input_sha256");
        var questionHash = AuditorJson.Sha256(data["question_schema_sha256"], "question_schema_sha256");
        if (inputHash != AuditorJson.Hash(JsonValue.Create(decision.State)!))
            throw new AuditorContractException("input_sha256 does not match decision state.");
        if (questionHash != AuditorJson.Hash(decision.QuestionSchemaNode()))
            throw new AuditorContractException("question_schema_sha256 does not match questions.");
        return new(AuditorSchema.AuditRequest, AuditorJson.Identifier(data["audit_id"], "audit_id"),
            AuditorJson.Identifier(data["agent_task_id"], "agent_task_id"), AuditorJson.Identifier(data["agent_worker_id"], "agent_worker_id"),
            AuditorJson.Identifier(data["stream_id"], "stream_id"), AuditorJson.Long(data["sequence"], "sequence"),
            AuditorJson.Long(data["input_revision"], "input_revision"), inputHash, questionHash,
            AuditorJson.Enum(data["risk_class"], "risk_class", AuditorSchema.RiskClasses),
            AuditorJson.Enum(data["minimum_tier"], "minimum_tier", AuditorSchema.AssuranceTiers),
            AuditorJson.Enum(data["execution_mode"], "execution_mode", AuditorSchema.ExecutionModes),
            AuditorJson.Long(data["deadline_mono_ms"], "deadline_mono_ms", 1), AuditorJson.Long(data["max_result_age_ms"], "max_result_age_ms", 1),
            AuditorJson.Identifier(data["data_zone"], "data_zone"), AuditorJson.Boolean(data["allow_cross_worker"], "allow_cross_worker"),
            AuditorJson.Integer(data["estimated_input_tokens"], "estimated_input_tokens", 1),
            new ReadOnlyDictionary<string, string>(kinds), AuditorJson.Identifiers(data["required_policy_ids"], "required_policy_ids", true), decision);
    }

    public string ToJson() => AuditorJson.Canonical(ToNode());
    internal JsonObject ToNode() => new()
    {
        ["schema_version"] = SchemaVersion, ["audit_id"] = AuditId, ["agent_task_id"] = AgentTaskId,
        ["agent_worker_id"] = AgentWorkerId, ["stream_id"] = StreamId, ["sequence"] = Sequence,
        ["input_revision"] = InputRevision, ["input_sha256"] = InputSha256, ["question_schema_sha256"] = QuestionSchemaSha256,
        ["risk_class"] = RiskClass, ["minimum_tier"] = MinimumTier, ["execution_mode"] = ExecutionMode,
        ["deadline_mono_ms"] = DeadlineMonoMs, ["max_result_age_ms"] = MaxResultAgeMs, ["data_zone"] = DataZone,
        ["allow_cross_worker"] = AllowCrossWorker, ["estimated_input_tokens"] = EstimatedInputTokens,
        ["check_kinds"] = Map(CheckKinds), ["required_policy_ids"] = Array(RequiredPolicyIds), ["decision_request"] = DecisionRequest.ToNode()
    };
    private static JsonObject Map(IEnumerable<KeyValuePair<string, string>> values)
    { var result = new JsonObject(); foreach (var item in values) result[item.Key] = item.Value; return result; }
    private static JsonArray Array(IEnumerable<string> values) => new(values.Select(value => JsonValue.Create(value)).ToArray<JsonNode?>());
}
