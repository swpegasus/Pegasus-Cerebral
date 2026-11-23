using System.Text.Json.Nodes;

namespace Cerebral.Auditing;

/// <summary>
/// Provider-neutral advisory boundary. It deliberately exposes no tool, filesystem,
/// queue, policy-mutation, authorization, or action-dispatch capability.
/// </summary>
public static class AdvisorContract
{
    public const string Capability = "cerebral.advisor.capability.v1";
    public const string Request = "cerebral.advisor.request.v1";
    public const string Result = "cerebral.advisor.result.v1";
    public const int ProtocolMajor = 1;
    public static readonly IReadOnlySet<string> ModuleKinds = new HashSet<string>(
        ["auditor", "decision", "relevance", "retention", "routing"], StringComparer.Ordinal);
    public static readonly IReadOnlySet<string> DeliveryDispositions = new HashSet<string>(
        ["accepted", "unavailable", "stale", "timed_out", "cancelled", "rejected", "failed"], StringComparer.Ordinal);
}

public sealed record AdvisorLimits(int MaxContextBytes, int MaxContextTokens, int MaxEvidenceRefs,
    int MaxQuestions, int MaxOptions, int MaxConcurrency);

public sealed record AdvisorResources(int ResidentMemoryMiB, int WorkingMemoryMiB, int ColdStartMs,
    int EstimatedP95Ms, string? SharedBackboneIdentity = null, int IncrementalMemoryMiB = 0);

public sealed record AdvisorCapability(string SchemaVersion, string ModuleId, string ModuleVersion,
    long InstanceGeneration, IReadOnlyList<string> ModuleKinds, int ProtocolMinMajor, int ProtocolMaxMajor,
    string ImplementationIdentity, string ImplementationSha256, string RuntimeIdentity, string ModelIdentity,
    IReadOnlyList<string> RequestSchemaIds, IReadOnlyList<string> ResultSchemaIds,
    IReadOnlyList<string> SupportedPurposes, IReadOnlyList<string> SupportedDecisionTypes,
    IReadOnlyList<string> SupportedDataZones, IReadOnlyList<string> ExecutionModes,
    AdvisorLimits Limits, AdvisorResources Resources, string AssuranceTier,
    bool AdvisoryOnly = true, bool DirectActionAllowed = false)
{
    public string ToJson() => AuditorJson.Canonical(ToNode());
    public string Sha256() { Validate(); return AuditorJson.Hash(ToNode()); }

    public void Validate()
    {
        if (SchemaVersion != AdvisorContract.Capability) throw new AuditorContractException("advisor capability version mismatch.");
        _ = AuditorJson.Identifier(JsonValue.Create(ModuleId), "module_id");
        _ = AuditorJson.Text(JsonValue.Create(ModuleVersion), "module_version", 64);
        if (InstanceGeneration < 1) throw new AuditorContractException("instance_generation must be positive.");
        if (ModuleKinds.Count == 0 || ModuleKinds.Distinct(StringComparer.Ordinal).Count() != ModuleKinds.Count ||
            ModuleKinds.Any(kind => !AdvisorContract.ModuleKinds.Contains(kind)))
            throw new AuditorContractException("module_kinds contains an unsupported or duplicate value.");
        if (ProtocolMinMajor != AdvisorContract.ProtocolMajor || ProtocolMaxMajor != AdvisorContract.ProtocolMajor)
            throw new AuditorContractException("advisor protocol major version is incompatible.");
        _ = AuditorJson.Text(JsonValue.Create(ImplementationIdentity), "implementation_identity", 256);
        _ = AuditorJson.Sha256(JsonValue.Create(ImplementationSha256), "implementation_sha256");
        _ = AuditorJson.Text(JsonValue.Create(RuntimeIdentity), "runtime_identity", 256);
        _ = AuditorJson.Text(JsonValue.Create(ModelIdentity), "model_identity", 256);
        RequireIdentifiers(RequestSchemaIds, "request_schema_ids"); RequireIdentifiers(ResultSchemaIds, "result_schema_ids");
        RequireIdentifiers(SupportedPurposes, "supported_purposes"); RequireIdentifiers(SupportedDecisionTypes, "supported_decision_types");
        RequireIdentifiers(SupportedDataZones, "supported_data_zones"); RequireIdentifiers(ExecutionModes, "execution_modes");
        if (Limits is not { MaxContextBytes: > 0, MaxContextTokens: > 0, MaxEvidenceRefs: >= 0,
                MaxQuestions: > 0, MaxOptions: > 1, MaxConcurrency: > 0 })
            throw new AuditorContractException("advisor limits are invalid.");
        if (Resources is not { ResidentMemoryMiB: >= 0, WorkingMemoryMiB: >= 0, ColdStartMs: >= 0, EstimatedP95Ms: > 0,
                IncrementalMemoryMiB: >= 0 })
            throw new AuditorContractException("advisor resource estimates are invalid.");
        if (Resources.SharedBackboneIdentity is null && Resources.IncrementalMemoryMiB != 0)
            throw new AuditorContractException("incremental memory requires a shared backbone identity.");
        _ = AuditorJson.Identifier(JsonValue.Create(AssuranceTier), "assurance_tier");
        if (!AdvisoryOnly || DirectActionAllowed)
            throw new AuditorContractException("advisor modules must remain advisory and cannot directly authorize actions.");
    }

    internal JsonObject ToNode() => new()
    {
        ["schema_version"] = SchemaVersion, ["module_id"] = ModuleId, ["module_version"] = ModuleVersion,
        ["instance_generation"] = InstanceGeneration, ["module_kinds"] = Array(ModuleKinds),
        ["protocol_min_major"] = ProtocolMinMajor, ["protocol_max_major"] = ProtocolMaxMajor,
        ["implementation_identity"] = ImplementationIdentity, ["implementation_sha256"] = ImplementationSha256,
        ["runtime_identity"] = RuntimeIdentity, ["model_identity"] = ModelIdentity,
        ["request_schema_ids"] = Array(RequestSchemaIds), ["result_schema_ids"] = Array(ResultSchemaIds),
        ["supported_purposes"] = Array(SupportedPurposes), ["supported_decision_types"] = Array(SupportedDecisionTypes),
        ["supported_data_zones"] = Array(SupportedDataZones), ["execution_modes"] = Array(ExecutionModes),
        ["limits"] = new JsonObject { ["max_context_bytes"] = Limits.MaxContextBytes, ["max_context_tokens"] = Limits.MaxContextTokens,
            ["max_evidence_refs"] = Limits.MaxEvidenceRefs, ["max_questions"] = Limits.MaxQuestions,
            ["max_options"] = Limits.MaxOptions, ["max_concurrency"] = Limits.MaxConcurrency },
        ["resources"] = new JsonObject { ["resident_memory_mib"] = Resources.ResidentMemoryMiB,
            ["working_memory_mib"] = Resources.WorkingMemoryMiB, ["cold_start_ms"] = Resources.ColdStartMs,
            ["estimated_p95_ms"] = Resources.EstimatedP95Ms, ["shared_backbone_identity"] = Resources.SharedBackboneIdentity,
            ["incremental_memory_mib"] = Resources.IncrementalMemoryMiB },
        ["assurance_tier"] = AssuranceTier, ["advisory_only"] = AdvisoryOnly,
        ["direct_action_allowed"] = DirectActionAllowed
    };

    private static void RequireIdentifiers(IReadOnlyList<string> values, string name)
    {
        if (values.Count == 0 || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new AuditorContractException(name + " must contain unique values.");
        foreach (var value in values) _ = AuditorJson.Identifier(JsonValue.Create(value), name + " item");
    }
    private static JsonArray Array(IEnumerable<string> values) =>
        new(values.Select(value => JsonValue.Create(value)).ToArray<JsonNode?>());
}

public sealed record AdvisorEvidenceReference(string RefId, long Revision, string Sha256, string MediaType,
    string DataZone, string Purpose);

public sealed record AdvisorRequest(string SchemaVersion, string RequestId, string IdempotencyKey, int Attempt,
    string ModuleId, string CapabilitySha256, string TaskId, string ConversationId, string StreamId,
    long Sequence, long InputRevision, string ContextSha256, string DecisionSchemaSha256,
    string AuthorityScopeId, string DataZone, string RiskClass, string Purpose, string ModuleKind,
    string ExecutionMode, long CreatedMonoMs, long DeadlineMonoMs, long MaxResultAgeMs, string CancellationId,
    string ContextSchemaId, string ContextJson, int ContextBytes, int ContextTokens,
    IReadOnlyList<AdvisorEvidenceReference> EvidenceRefs, IReadOnlyList<string> PolicyRefs,
    string DomainRequestSchemaId, string DomainRequestJson)
{
    public void Validate(AdvisorCapability capability)
    {
        capability.Validate();
        if (SchemaVersion != AdvisorContract.Request) throw new AuditorContractException("advisor request version mismatch.");
        _ = AuditorJson.Identifier(JsonValue.Create(RequestId), "request_id");
        _ = AuditorJson.Identifier(JsonValue.Create(IdempotencyKey), "idempotency_key");
        if (Attempt < 1) throw new AuditorContractException("attempt must be positive.");
        if (ModuleId != capability.ModuleId || CapabilitySha256 != capability.Sha256())
            throw new AuditorContractException("advisor request capability identity mismatch.");
        foreach (var identity in new[] { TaskId, ConversationId, StreamId, AuthorityScopeId, CancellationId, ContextSchemaId, DomainRequestSchemaId })
            _ = AuditorJson.Identifier(JsonValue.Create(identity), "advisor request identity");
        if (Sequence < 0 || InputRevision < 0 || CreatedMonoMs < 0 || DeadlineMonoMs <= CreatedMonoMs || MaxResultAgeMs < 1)
            throw new AuditorContractException("advisor request counters or deadline are invalid.");
        _ = AuditorJson.Sha256(JsonValue.Create(ContextSha256), "context_sha256");
        _ = AuditorJson.Sha256(JsonValue.Create(DecisionSchemaSha256), "decision_schema_sha256");
        if (!capability.SupportedDataZones.Contains(DataZone, StringComparer.Ordinal) ||
            !capability.SupportedPurposes.Contains(Purpose, StringComparer.Ordinal) ||
            !capability.ModuleKinds.Contains(ModuleKind, StringComparer.Ordinal) ||
            !capability.ExecutionModes.Contains(ExecutionMode, StringComparer.Ordinal) ||
            !capability.RequestSchemaIds.Contains(DomainRequestSchemaId, StringComparer.Ordinal))
            throw new AuditorContractException("advisor request is outside the admitted capability.");
        JsonNode contextNode;
        try { contextNode = JsonNode.Parse(ContextJson) ?? throw new AuditorContractException("advisor context must be valid JSON."); }
        catch (System.Text.Json.JsonException ex) { throw new AuditorContractException("advisor context is not valid JSON: " + ex.Message); }
        var canonicalContext = AuditorJson.Canonical(contextNode);
        if (canonicalContext != ContextJson || AuditorJson.Hash(contextNode) != ContextSha256)
            throw new AuditorContractException("advisor context is not canonical or its digest does not match.");
        if (ContextBytes != System.Text.Encoding.UTF8.GetByteCount(ContextJson) || ContextBytes > capability.Limits.MaxContextBytes ||
            ContextTokens < 0 || ContextTokens > capability.Limits.MaxContextTokens)
            throw new AuditorContractException("advisor context size exceeds the admitted capability.");
        if (EvidenceRefs.Count > capability.Limits.MaxEvidenceRefs || EvidenceRefs.Select(item => item.RefId).Distinct(StringComparer.Ordinal).Count() != EvidenceRefs.Count)
            throw new AuditorContractException("advisor evidence references are duplicate or exceed the admitted bound.");
        foreach (var evidence in EvidenceRefs)
        {
            _ = AuditorJson.Identifier(JsonValue.Create(evidence.RefId), "evidence ref_id");
            _ = AuditorJson.Sha256(JsonValue.Create(evidence.Sha256), "evidence sha256");
            if (evidence.Revision < 0 || evidence.DataZone != DataZone)
                throw new AuditorContractException("advisor evidence revision or data zone is invalid.");
        }
        if (!capability.RequestSchemaIds.Contains(DomainRequestSchemaId, StringComparer.Ordinal))
            throw new AuditorContractException("domain request schema is not admitted.");
        if (AuditorJson.Canonical(AuditorJson.ParseObject(DomainRequestJson, "domain request")) != DomainRequestJson)
            throw new AuditorContractException("domain request must use canonical JSON.");
    }
}

public sealed record AdvisorHostState(long CurrentRevision, string CurrentContextSha256, long CurrentSequence,
    long NowMonoMs, bool Cancelled = false);

public sealed record AdvisorResult(string SchemaVersion, string RequestId, string IdempotencyKey, int Attempt,
    string ModuleId, long InstanceGeneration, string CapabilitySha256, string Disposition, string ReasonCode,
    string? DomainResultSchemaId, string? DomainResultJson, string? DomainResultSha256,
    bool AdvisoryOnly = true, bool DirectActionAllowed = false);

public interface IAdvisorModule
{
    AdvisorCapability Capability { get; }
    ValueTask<AdvisorResult> InvokeAsync(AdvisorRequest request, AdvisorHostState hostState,
        CancellationToken cancellationToken);
}

/// <summary>Maps the frozen STRATA audit request/result pair onto the generic advisor boundary.</summary>
public sealed class AuditingAdvisorAdapter(AdvisorCapability capability,
    Func<AuditRequest, CancellationToken, ValueTask<AuditResult>> evaluator) : IAdvisorModule
{
    public AdvisorCapability Capability { get; } = capability;

    public async ValueTask<AdvisorResult> InvokeAsync(AdvisorRequest request, AdvisorHostState hostState,
        CancellationToken cancellationToken)
    {
        request.Validate(Capability);
        if (request.DomainRequestSchemaId != AuditorSchema.AuditRequest ||
            !Capability.ResultSchemaIds.Contains(AuditorSchema.AuditResult, StringComparer.Ordinal))
            throw new AuditorContractException("the auditing adapter requires the frozen Cerebral audit schemas.");
        var audit = AuditRequest.Parse(request.DomainRequestJson);
        if (audit.AgentTaskId != request.TaskId || audit.StreamId != request.StreamId ||
            audit.Sequence != request.Sequence || audit.InputRevision != request.InputRevision ||
            audit.InputSha256 != request.ContextSha256 || audit.QuestionSchemaSha256 != request.DecisionSchemaSha256 ||
            audit.DataZone != request.DataZone || audit.RiskClass != request.RiskClass ||
            audit.ExecutionMode != request.ExecutionMode || audit.DeadlineMonoMs != request.DeadlineMonoMs ||
            audit.MaxResultAgeMs != request.MaxResultAgeMs)
            throw new AuditorContractException("generic advisor identity does not match the audit-domain request.");
        if (hostState.Cancelled || cancellationToken.IsCancellationRequested)
            return Terminal(request, "cancelled", "host_cancelled", null);
        if (hostState.NowMonoMs > request.DeadlineMonoMs)
            return Terminal(request, "timed_out", "deadline_exceeded", null);

        var raw = await evaluator(audit, cancellationToken);
        var validated = AuditResult.Parse(raw.ToJson(), audit);
        var domainJson = validated.ToJson();
        var delivery = AuditDelivery.Resolve(audit, validated, hostState.CurrentRevision,
            hostState.CurrentContextSha256, hostState.CurrentSequence, hostState.NowMonoMs);
        return Terminal(request, delivery.Status, delivery.ReasonCode, domainJson);
    }

    private AdvisorResult Terminal(AdvisorRequest request, string disposition, string reason, string? domainJson)
    {
        if (!AdvisorContract.DeliveryDispositions.Contains(disposition))
            throw new AuditorContractException("advisor delivery disposition is invalid.");
        return new(AdvisorContract.Result, request.RequestId, request.IdempotencyKey, request.Attempt,
            Capability.ModuleId, Capability.InstanceGeneration, request.CapabilitySha256, disposition, reason,
            domainJson is null ? null : AuditorSchema.AuditResult, domainJson,
            domainJson is null ? null : AuditorJson.Hash(JsonNode.Parse(domainJson)!), true, false);
    }
}
