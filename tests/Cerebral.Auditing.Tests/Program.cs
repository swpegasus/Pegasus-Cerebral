using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;
using System.Diagnostics;
using Cerebral.Auditing;

if (args.Contains("--workflow-advisor", StringComparer.Ordinal))
{
    await WorkflowAdvisorAdapterTests.RunAsync();
    return;
}

var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new Exception("FAIL: " + label);
    checks++; Console.WriteLine("PASS: " + label);
}
void Reject(Action action, string label)
{
    try { action(); throw new Exception("FAIL: " + label); }
    catch (AuditorContractException) { checks++; Console.WriteLine("PASS: " + label); }
}

var decision = DecisionRequest.Parse(JsonNode.Parse("""
{
  "schema_version":"strata.decision.v1",
  "request_id":"decision.1",
  "state":"The proposed change lacks a backup and a rollback plan.",
  "questions":[{"id":"evidence","type":"boolean","instructions":"Is the evidence sufficient to authorize the change?"}]
}
""")!);
var request = AuditRequest.Create(decision, "audit.1", "task.1", "worker.agent", "stream.1", 7, 3,
    "low", "compact", "asynchronous", 10_000, 1_000, "private", true, 128,
    new Dictionary<string, string> { ["evidence"] = "evidence_sufficiency" }, ["policy.file-write"]);
Check(request.InputSha256 == "d4f00aac769e8c2cca4970333e20c00a32704784da8659bc6e5d825b613f14e3" &&
      request.QuestionSchemaSha256 == "f35a9de35196f8545583a336c2de6a644127b4c591da2ce8c03fcf4f4d7350b5",
    "Cerebral canonical state and question-schema digests match the frozen STRATA Python fixture");
Check(AuditRequest.Parse(request.ToJson()).ToJson() == request.ToJson(),
    "audit request round-trips through strict canonical JSON");
var requestWithExtra = JsonNode.Parse(request.ToJson())!.AsObject(); requestWithExtra["hidden_fallback"] = true;
Reject(() => AuditRequest.Parse(requestWithExtra.ToJsonString()), "unknown audit-request fields fail closed");
var requestWithBadHash = JsonNode.Parse(request.ToJson())!.AsObject(); requestWithBadHash["input_sha256"] = new string('0', 64);
Reject(() => AuditRequest.Parse(requestWithBadHash.ToJsonString()), "audit request state digest is recomputed at admission");

var worker = WorkerSnapshot.Parse("""
{
  "schema_version":"cerebral.worker.snapshot.v1","worker_id":"worker.agent","enabled":true,
  "hardware_class":"fixture-cpu","location":"fixture-local","data_zones":["private"],
  "memory_total_mib":16000,"memory_available_mib":8000,"reserved_agent_memory_mib":2000,
  "audit_slots":1,"active_audits":0,"installed_backend_ids":["backend.compact"],
  "resident_backend_ids":[],"active_model_identities":[]
}
""");
var backend = AuditBackendSnapshot.Parse("""
{
  "schema_version":"cerebral.audit.backend.snapshot.v1","backend_id":"backend.compact","host_worker_id":"worker.agent",
  "enabled":true,"assurance_tier":"compact","model_identity":"fixture/backend@revision","runtime_identity":"fixture.runtime.v1",
  "resident_memory_mib":700,"working_memory_mib":200,"shared_backbone_model_identity":null,
  "shared_backbone_incremental_mib":0,"max_input_tokens":512,"max_options":8,
  "audit_kinds":["evidence_sufficiency","contradiction","unsafe_action_risk","stale_context","policy_mismatch","escalation_need"],
  "data_zones":["private"],"execution_modes":["asynchronous","synchronous"],"p95_latency_ms":100,
  "cold_start_ms":500,"queue_capacity":2,"queue_depth":0,"calibration_scope_id":null
}
""");
var policy = AuditorPolicy.Parse("""
{
  "schema_version":"cerebral.audit.selection-policy.v1",
  "risk_minimum_tier":{"low":"compact","medium":"compact","high":"standard","critical":"robust"},
  "synchronous_risks":["high","critical"],"cross_worker_risks":["low","medium","high","critical"],
  "no_silent_fallback_risks":["high","critical"],"minimum_memory_headroom_mib":512,
  "agent_colocation_penalty_ms":25
}
""");
Check(WorkerSnapshot.Parse(worker.ToJson()).ToJson() == worker.ToJson() &&
      AuditBackendSnapshot.Parse(backend.ToJson()).ToJson() == backend.ToJson() &&
      AuditorPolicy.Parse(policy.ToJson()).ToJson() == policy.ToJson(),
    "worker, backend, and policy schemas round-trip without widening fields");
var selected = AuditorSelector.Select(request, policy, [worker], [backend], 100);
Check(selected is { Status: "selected", AuditWorkerId: "worker.agent", BackendId: "backend.compact", RequiredMemoryMiB: 900 },
    "deterministic selection admits the compatible local backend with measured memory cost");
var highAsync = AuditRequest.Create(decision, "audit.high", "task.1", "worker.agent", "stream.1", 8, 4,
    "high", "compact", "asynchronous", 10_000, 1_000, "private", true, 128,
    new Dictionary<string, string> { ["evidence"] = "evidence_sufficiency" });
Check(AuditorSelector.Select(highAsync, policy, [worker], [backend], 100).ReasonCodes.SequenceEqual(["synchronous_mode_required"]),
    "high-risk asynchronous requests reject without lowering assurance");
var constrainedWorker = WorkerSnapshot.Parse(worker.ToJson().Replace("\"memory_available_mib\":8000", "\"memory_available_mib\":1000"));
Check(AuditorSelector.Select(request, policy, [constrainedWorker], [backend], 100) is { Status: "wait" },
    "transient memory pressure waits instead of pretending the backend is unavailable forever");
Reject(() => AuditorSelector.Select(request, policy, [worker with { MemoryAvailableMiB = 99_999 }], [backend], 100),
    "selector revalidates programmatically constructed snapshots at its public boundary");
var badWorker = JsonNode.Parse(worker.ToJson())!.AsObject(); badWorker["resident_backend_ids"] = new JsonArray("backend.other");
Reject(() => WorkerSnapshot.Parse(badWorker.ToJsonString()), "worker snapshots reject resident backends that are not installed");
var badBackend = JsonNode.Parse(backend.ToJson())!.AsObject(); badBackend["queue_depth"] = 3;
Reject(() => AuditBackendSnapshot.Parse(badBackend.ToJsonString()), "backend snapshots reject impossible queue depth");

JsonObject Response() => JsonNode.Parse("""
{
  "schema_version":"strata.decision.v1","request_id":"decision.1","mode":"direct",
  "results":[{
    "question_id":"evidence","status":"ok","predicted_option_id":"false","accepted_option_id":"false",
    "candidate_logits":{"true":0.0,"false":1.0},"uncalibrated_probabilities":{"true":0.25,"false":0.75},"error":null
  }]
}
""")!.AsObject();
JsonObject Result(bool directAction = false)
{
    return new JsonObject
    {
        ["schema_version"] = AuditorSchema.AuditResult, ["audit_id"] = request.AuditId,
        ["agent_task_id"] = request.AgentTaskId, ["agent_worker_id"] = request.AgentWorkerId,
        ["audit_worker_id"] = "worker.agent", ["backend_id"] = "backend.compact",
        ["input_revision"] = request.InputRevision, ["input_sha256"] = request.InputSha256,
        ["sequence"] = request.Sequence, ["status"] = "completed", ["decision_response"] = Response(),
        ["error_code"] = null, ["completed_mono_ms"] = 900, ["advisory_only"] = true,
        ["direct_action_allowed"] = directAction
    };
}
var result = AuditResult.Parse(Result().ToJsonString(), request);
Check(result.AdvisoryOnly && !result.DirectActionAllowed && AuditResult.Parse(result.ToJson(), request).Status == "completed",
    "completed semantic audit results remain advisory and round-trip with request identity");
Reject(() => AuditResult.Parse(Result(true).ToJsonString(), request), "semantic audit results cannot grant direct action authority");
Check(AuditDelivery.Resolve(request, result, 3, request.InputSha256, 7, 950) == new DeliveryDecision("accepted", "current_revision"),
    "current bounded audit result is accepted");
Check(AuditDelivery.Resolve(request, result, 4, request.InputSha256, 8, 950) == new DeliveryDecision("stale", "superseded_input"),
    "superseded stream state rejects a late audit result");
Reject(() => AuditDelivery.Resolve(request, result with { DirectActionAllowed = true }, 3, request.InputSha256, 7, 950),
    "delivery revalidates programmatically constructed results and rejects forged authority");
Check(typeof(AuditRequest).Assembly.GetReferencedAssemblies().All(item =>
        !item.Name!.StartsWith("Cerebral.", StringComparison.Ordinal) || item.Name == "Cerebral.Contracts"),
    "optional auditing bridge depends only on shared contracts, not the Worker, engine, desktop, core, or a plugin runtime");

var advisorCapability = new AdvisorCapability(AdvisorContract.Capability, "fixture.advisor", "1.0.0", 1,
    ["auditor", "decision", "relevance", "retention", "routing"], 1, 1,
    "fixture.advisor.dll", new string('a', 64), "fixture.in-process.v1", "none",
    [AuditorSchema.AuditRequest], [AuditorSchema.AuditResult],
    ["audit", "decide", "rank", "retain", "route"], ["boolean"], ["private", "family"],
    ["asynchronous", "synchronous"], new(32_768, 4_096, 8, 8, 8, 1),
    new(0, 4, 0, 1), "deterministic");
var capabilityDigest = advisorCapability.Sha256();
Check(advisorCapability.AdvisoryOnly && !advisorCapability.DirectActionAllowed && capabilityDigest.Length == 64,
    "generic advisor capability is digest-bound and exposes no direct-action authority");

AuditResult CanaryResult(AuditRequest audit, bool abstain)
{
    var resultStatus = abstain ? "abstained" : "ok";
    var response = new JsonObject
    {
        ["schema_version"] = DecisionRequest.Version,
        ["request_id"] = audit.DecisionRequest.RequestId,
        ["mode"] = "direct",
        ["results"] = new JsonArray(new JsonObject
        {
            ["question_id"] = "decision", ["status"] = resultStatus,
            ["predicted_option_id"] = abstain ? null : "false",
            ["accepted_option_id"] = abstain ? null : "false",
            ["candidate_logits"] = new JsonObject { ["true"] = 0.0, ["false"] = 1.0 },
            ["uncalibrated_probabilities"] = new JsonObject { ["true"] = 0.25, ["false"] = 0.75 },
            ["error"] = null
        })
    };
    return AuditResult.Parse(new JsonObject
    {
        ["schema_version"] = AuditorSchema.AuditResult, ["audit_id"] = audit.AuditId,
        ["agent_task_id"] = audit.AgentTaskId, ["agent_worker_id"] = audit.AgentWorkerId,
        ["audit_worker_id"] = "worker.agent", ["backend_id"] = "backend.compact",
        ["input_revision"] = audit.InputRevision, ["input_sha256"] = audit.InputSha256,
        ["sequence"] = audit.Sequence, ["status"] = "completed", ["decision_response"] = response,
        ["error_code"] = null, ["completed_mono_ms"] = 900,
        ["advisory_only"] = true, ["direct_action_allowed"] = false
    }.ToJsonString(), audit);
}

var advisor = new AuditingAdvisorAdapter(advisorCapability,
    (audit, _) => ValueTask.FromResult(CanaryResult(audit, audit.AuditId.EndsWith(".abstain", StringComparison.Ordinal))));

AuditResult UnavailableResult(AuditRequest audit, string errorCode) => AuditResult.Parse(new JsonObject
{
    ["schema_version"] = AuditorSchema.AuditResult, ["audit_id"] = audit.AuditId,
    ["agent_task_id"] = audit.AgentTaskId, ["agent_worker_id"] = audit.AgentWorkerId,
    ["audit_worker_id"] = null, ["backend_id"] = null,
    ["input_revision"] = audit.InputRevision, ["input_sha256"] = audit.InputSha256,
    ["sequence"] = audit.Sequence, ["status"] = "unavailable", ["decision_response"] = null,
    ["error_code"] = errorCode, ["completed_mono_ms"] = 900,
    ["advisory_only"] = true, ["direct_action_allowed"] = false
}.ToJsonString(), audit);

AdvisorRequest AdvisorPacket(AuditRequest audit, string suffix)
{
    var context = JsonSerializer.Serialize(audit.DecisionRequest.State);
    return new(AdvisorContract.Request, "advisor." + suffix, "idem." + suffix, 1,
        advisorCapability.ModuleId, capabilityDigest, audit.AgentTaskId, "conversation." + suffix,
        audit.StreamId, audit.Sequence, audit.InputRevision, audit.InputSha256, audit.QuestionSchemaSha256,
        "scope." + audit.DataZone, audit.DataZone, audit.RiskClass, "audit", "auditor", audit.ExecutionMode,
        100, audit.DeadlineMonoMs, audit.MaxResultAgeMs, "cancel." + suffix, "strata.state.v1", context,
        Encoding.UTF8.GetByteCount(context), 32, [], [], AuditorSchema.AuditRequest, audit.ToJson());
}

var disabledAudit = AuditRequest.Create(decision, "audit.provider-disabled", "task.terminal", "worker.agent", "stream.terminal", 20, 5,
    "low", "compact", "asynchronous", 10_000, 1_000, "private", false, 96,
    new Dictionary<string, string> { ["evidence"] = "evidence_sufficiency" });
var unavailableAudit = AuditRequest.Create(decision, "audit.provider-unavailable", "task.terminal", "worker.agent", "stream.terminal", 21, 6,
    "low", "compact", "asynchronous", 10_000, 1_000, "private", false, 96,
    new Dictionary<string, string> { ["evidence"] = "evidence_sufficiency" });
var disabledRequest = AdvisorPacket(disabledAudit, "provider-disabled");
var unavailableRequest = AdvisorPacket(unavailableAudit, "provider-unavailable");
var providerCalls = 0;
var unavailableAdvisor = new AuditingAdvisorAdapter(advisorCapability, (audit, _) =>
{
    providerCalls++;
    var reason = audit.AuditId.EndsWith("provider-disabled", StringComparison.Ordinal)
        ? "provider_disabled" : "provider_unavailable";
    return ValueTask.FromResult(UnavailableResult(audit, reason));
});
var disabledDomain = UnavailableResult(disabledAudit, "provider_disabled");
var disabledDelivery = await unavailableAdvisor.InvokeAsync(disabledRequest,
    new(disabledAudit.InputRevision, disabledAudit.InputSha256, disabledAudit.Sequence, 950), CancellationToken.None);
var unavailableDomain = UnavailableResult(unavailableAudit, "provider_unavailable");
var unavailableDelivery = await unavailableAdvisor.InvokeAsync(unavailableRequest,
    new(unavailableAudit.InputRevision, unavailableAudit.InputSha256, unavailableAudit.Sequence, 950), CancellationToken.None);
Check(providerCalls == 2 &&
      disabledDelivery is { Disposition: "unavailable", ReasonCode: "provider_disabled", AdvisoryOnly: true, DirectActionAllowed: false } &&
      unavailableDelivery is { Disposition: "unavailable", ReasonCode: "provider_unavailable", AdvisoryOnly: true, DirectActionAllowed: false } &&
      disabledDelivery.DomainResultJson == disabledDomain.ToJson() && unavailableDelivery.DomainResultJson == unavailableDomain.ToJson() &&
      disabledDelivery.DomainResultSchemaId == AuditorSchema.AuditResult && disabledDelivery.DomainResultSha256 is { Length: 64 } &&
      disabledDelivery.RequestId == disabledRequest.RequestId && disabledDelivery.IdempotencyKey == disabledRequest.IdempotencyKey &&
      disabledDelivery.ModuleId == advisorCapability.ModuleId && disabledDelivery.CapabilitySha256 == capabilityDigest &&
      disabledDomain is { Status: "unavailable", DecisionResponse: null } && unavailableDomain is { Status: "unavailable", DecisionResponse: null },
    "disabled and unavailable providers preserve distinct domain reasons under the advisory unavailable disposition without fabricating a response");

var staleUnavailable = await unavailableAdvisor.InvokeAsync(unavailableRequest,
    new(unavailableAudit.InputRevision + 1, unavailableAudit.InputSha256, unavailableAudit.Sequence + 1, 950), CancellationToken.None);
Check(providerCalls == 3 && staleUnavailable is { Disposition: "stale", ReasonCode: "superseded_input", AdvisoryOnly: true, DirectActionAllowed: false } &&
      staleUnavailable.DomainResultJson == unavailableDomain.ToJson(),
    "superseded host state remains stale even when the actual provider result is unavailable");

using var cancelledToken = new CancellationTokenSource(); cancelledToken.Cancel();
var cancelledUnavailable = await unavailableAdvisor.InvokeAsync(unavailableRequest,
    new(unavailableAudit.InputRevision, unavailableAudit.InputSha256, unavailableAudit.Sequence, 950), cancelledToken.Token);
Check(providerCalls == 3 && cancelledUnavailable is { Disposition: "cancelled", ReasonCode: "host_cancelled", AdvisoryOnly: true, DirectActionAllowed: false,
        DomainResultJson: null, DomainResultSchemaId: null, DomainResultSha256: null },
    "host cancellation returns no fabricated provider result and never invokes the unavailable provider");

var forgedActionAdvisor = new AuditingAdvisorAdapter(advisorCapability,
    (audit, _) => ValueTask.FromResult(UnavailableResult(audit, "provider_unavailable") with { DirectActionAllowed = true }));
var forgedActionRejected = false;
try
{
    _ = await forgedActionAdvisor.InvokeAsync(unavailableRequest,
        new(unavailableAudit.InputRevision, unavailableAudit.InputSha256, unavailableAudit.Sequence, 950), CancellationToken.None);
}
catch (AuditorContractException) { forgedActionRejected = true; }
Check(forgedActionRejected, "unavailable provider output still cannot claim direct-action authority");

var kinds = new[] { "auditor", "decision", "relevance", "retention", "routing" };
var purposes = new[] { "audit", "decide", "rank", "retain", "route" };
var canaryPackets = 0;
var stopwatch = Stopwatch.StartNew();
for (var familyIndex = 0; familyIndex < kinds.Length; familyIndex++)
{
    for (var caseIndex = 0; caseIndex < 4; caseIndex++)
    {
        var suffix = caseIndex == 1 ? "abstain" : caseIndex == 2 ? "stale" : caseIndex == 3 ? "terminal" : "normal";
        var dataZone = familyIndex % 2 == 0 ? "private" : "family";
        var stream = familyIndex % 2 == 0 ? "stream.alpha" : "stream.beta";
        var canaryDecision = DecisionRequest.Parse(JsonNode.Parse($$"""
        {
          "schema_version":"strata.decision.v1",
          "request_id":"decision.{{familyIndex}}.{{caseIndex}}",
          "state":"{{stream}} permits only its own {{dataZone}} canary fact {{familyIndex}}/{{caseIndex}}.",
          "questions":[{"id":"decision","type":"boolean","instructions":"Is the bounded evidence sufficient?"}]
        }
        """)!);
        var canaryAudit = AuditRequest.Create(canaryDecision, $"audit.{familyIndex}.{suffix}", $"task.{familyIndex}",
            "worker.agent", stream, familyIndex * 10 + caseIndex, caseIndex + 1, "low", "compact", "asynchronous",
            10_000, 1_000, dataZone, false, 96, new Dictionary<string, string> { ["decision"] = "evidence_sufficiency" });
        var contextJson = JsonSerializer.Serialize(canaryDecision.State);
        var advisorRequest = new AdvisorRequest(AdvisorContract.Request, $"advisor.{familyIndex}.{caseIndex}",
            $"idem.{familyIndex}.{caseIndex}", 1, advisorCapability.ModuleId, capabilityDigest,
            canaryAudit.AgentTaskId, $"conversation.{familyIndex}", canaryAudit.StreamId, canaryAudit.Sequence,
            canaryAudit.InputRevision, canaryAudit.InputSha256, canaryAudit.QuestionSchemaSha256,
            $"scope.{dataZone}", dataZone, canaryAudit.RiskClass, purposes[familyIndex], kinds[familyIndex],
            canaryAudit.ExecutionMode, 100, canaryAudit.DeadlineMonoMs, canaryAudit.MaxResultAgeMs,
            $"cancel.{familyIndex}.{caseIndex}", "strata.state.v1", contextJson, Encoding.UTF8.GetByteCount(contextJson), 32,
            [new($"evidence.{familyIndex}.{caseIndex}", 1, new string((char)('b' + familyIndex), 64), "text/plain", dataZone, purposes[familyIndex])],
            ["policy.fixture"], AuditorSchema.AuditRequest, canaryAudit.ToJson());
        var cancelled = caseIndex == 3 && familyIndex % 2 == 0;
        var now = caseIndex == 3 && !cancelled ? 10_001 : 950;
        var currentRevision = caseIndex == 2 ? canaryAudit.InputRevision + 1 : canaryAudit.InputRevision;
        var hostState = new AdvisorHostState(currentRevision, canaryAudit.InputSha256, canaryAudit.Sequence, now, cancelled);
        var adapted = await advisor.InvokeAsync(advisorRequest, hostState, CancellationToken.None);
        var repeated = await advisor.InvokeAsync(advisorRequest, hostState, CancellationToken.None);
        var expectedDisposition = cancelled ? "cancelled" : now > canaryAudit.DeadlineMonoMs ? "timed_out" :
            AuditDelivery.Resolve(canaryAudit, CanaryResult(canaryAudit, caseIndex == 1), currentRevision,
                canaryAudit.InputSha256, canaryAudit.Sequence, now).Status;
        Check(adapted == repeated && adapted.Disposition == expectedDisposition &&
              adapted.AdvisoryOnly && !adapted.DirectActionAllowed,
            $"advisor E0 {kinds[familyIndex]} {suffix} is deterministic and preserves host delivery disposition");
        if (caseIndex < 3)
        {
            var directJson = CanaryResult(canaryAudit, caseIndex == 1).ToJson();
            Check(adapted.DomainResultJson == directJson && adapted.DomainResultSha256 is { Length: 64 },
                $"advisor E0 {kinds[familyIndex]} {suffix} preserves byte-identical domain output and its digest");
        }
        else Check(adapted.DomainResultJson is null,
            $"advisor E0 {kinds[familyIndex]} {suffix} executes no semantic provider after host timeout or cancellation");
        canaryPackets++;
    }
}
stopwatch.Stop();
Check(canaryPackets == 20, "advisor E0 covers twenty bounded packets across five module kinds");

var zoneDecision = DecisionRequest.Parse(JsonNode.Parse("""
{"schema_version":"strata.decision.v1","request_id":"decision.zone","state":"private fact","questions":[{"id":"decision","type":"boolean","instructions":"Enough?"}]}
""")!);
var zoneAudit = AuditRequest.Create(zoneDecision, "audit.zone", "task.zone", "worker.agent", "stream.zone", 1, 1,
    "low", "compact", "asynchronous", 10_000, 1_000, "private", false, 32,
    new Dictionary<string, string> { ["decision"] = "evidence_sufficiency" });
var zoneContext = JsonSerializer.Serialize(zoneDecision.State);
var crossZone = new AdvisorRequest(AdvisorContract.Request, "advisor.zone", "idem.zone", 1, advisorCapability.ModuleId,
    capabilityDigest, zoneAudit.AgentTaskId, "conversation.zone", zoneAudit.StreamId, zoneAudit.Sequence,
    zoneAudit.InputRevision, zoneAudit.InputSha256, zoneAudit.QuestionSchemaSha256, "scope.private", "private", "low",
    "audit", "auditor", "asynchronous", 100, 10_000, 1_000, "cancel.zone", "strata.state.v1", zoneContext,
    Encoding.UTF8.GetByteCount(zoneContext), 4,
    [new("evidence.cross-zone", 1, new string('f', 64), "text/plain", "family", "audit")],
    ["policy.fixture"], AuditorSchema.AuditRequest, zoneAudit.ToJson());
Reject(() => crossZone.Validate(advisorCapability), "advisor boundary rejects cross-zone evidence before module invocation");
Reject(() => (advisorCapability with { DirectActionAllowed = true }).Validate(),
    "advisor capability cannot claim direct-action authority");

checks += await WorkflowAdvisorAdapterTests.RunAsync();
Console.WriteLine($"Cerebral optional auditor/advisor contract checks passed: {checks}; E0 packets={canaryPackets}; adapter wall={stopwatch.Elapsed.TotalMilliseconds:F2} ms");
