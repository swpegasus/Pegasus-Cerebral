using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Auditing;

/// <summary>The preparing provider must measure its complete encoded input with its actual tokenizer.
/// ContextTokens separately measures the complete canonical host context. An explicit InputProjection
/// can encode only its exact packet, excluding host bookkeeping from native model input. Neither
/// count is a byte estimate, and no packet content may be omitted or truncated.</summary>
public sealed record WorkflowAdvisorPreparedRequest(AdvisorRequest Request, int EncodedInputTokens)
{
    public WorkflowAdvisorInputProjection? InputProjection { get; init; }
}

/// <summary>Actual-tokenizer count and digest of the exact canonical workflow Context packet.
/// This is a projection of the host envelope, not a summary or an independently editable prompt.</summary>
public sealed record WorkflowAdvisorInputProjection(string Json, string Sha256, int Tokens);

public enum WorkflowAdvisorCriterion { Met, Gap, InsufficientEvidence }

/// <summary>Optional native advisory bridge. Domain preparation and typed interpretation belong to the
/// provider; journal freshness and conditional delivery belong to the workflow host. No STRATA mapping,
/// model acquisition, execution handles or provider admission is supplied by this adapter.</summary>
public sealed class WorkflowAdvisorAdapter : IWorkflowAuditAdapter
{
    private readonly IAdvisorModule module;
    private readonly string moduleId;
    private readonly string packageId;
    private readonly Func<WorkflowAuditRequest, AdvisorCapability, CancellationToken, ValueTask<WorkflowAdvisorPreparedRequest>> prepare;
    private readonly Func<AdvisorRequest, AdvisorResult, WorkflowAdvisorCriterion> readCriterion;
    private readonly TimeProvider time;

    public WorkflowAdvisorAdapter(IAdvisorModule module, string packageId,
        Func<WorkflowAuditRequest, AdvisorCapability, CancellationToken, ValueTask<WorkflowAdvisorPreparedRequest>> prepare,
        Func<AdvisorRequest, AdvisorResult, WorkflowAdvisorCriterion> readCriterion, TimeProvider? timeProvider = null)
    {
        this.module = module ?? throw new ArgumentNullException(nameof(module));
        this.packageId = AuditorJson.Identifier(JsonValue.Create(packageId), nameof(packageId));
        this.prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
        this.readCriterion = readCriterion ?? throw new ArgumentNullException(nameof(readCriterion));
        time = timeProvider ?? TimeProvider.System;
        var capability = module.Capability;
        capability.Validate();
        if (!capability.ModuleKinds.Contains("auditor", StringComparer.Ordinal))
            throw new AuditorContractException("Workflow review requires an admitted auditor capability.");
        moduleId = capability.ModuleId;
    }

    // A live descriptor lets the host's final conditional-commit gate detect provider replacement
    // even when it happens after ReviewAsync returns. Collections returned here cannot mutate admission.
    public WorkflowAuditDescriptor Descriptor
    {
        get
        {
            var capability = CaptureCapability();
            return new(moduleId, packageId, capability.ModuleVersion, capability.InstanceGeneration,
                capability.Sha256(), Array.AsReadOnly(capability.SupportedPurposes.ToArray()));
        }
    }

    /// <summary>The request factory must use this exact canonical envelope as AdvisorRequest.ContextJson.
    /// The workflow witness hash differs from the hash of its bounded context projection; both are bound.
    /// ProposalSha256 is not substituted for the provider's decision-schema digest.</summary>
    public static string CreateContextJson(WorkflowAuditRequest request)
    {
        ValidateWorkflowRequest(request);
        return AuditorJson.Canonical(new JsonObject
        {
            ["schema_version"] = "cerebral.workflow-advisor.context.v1",
            ["request_id"] = request.RequestId, ["run_id"] = request.RunId,
            ["conversation_id"] = request.ConversationId, ["run_revision"] = request.RunRevision,
            ["conversation_revision"] = request.ConversationRevision, ["engine_epoch"] = request.EngineEpoch,
            ["purpose"] = request.Purpose, ["input_sha256"] = request.InputSha256,
            ["proposal_sha256"] = request.ProposalSha256,
            ["packet"] = JsonNode.Parse(request.Context.GetRawText())
        });
    }

    public async Task<WorkflowAuditResult> ReviewAsync(WorkflowAuditRequest request, CancellationToken cancellationToken)
    {
        ValidateWorkflowRequest(request);
        request = request with { Context = request.Context.Clone() };
        var capability = CaptureCapability();
        var fingerprint = capability.Sha256();
        WorkflowAuditResult Outcome(string disposition, string summary) => new(request.RequestId, moduleId,
            capability.InstanceGeneration, fingerprint, request.InputSha256, request.ProposalSha256,
            disposition, summary, true, false);
        if (cancellationToken.IsCancellationRequested) return Outcome("cancelled", "Auditor review was cancelled; no assessment was accepted.");
        if (Now >= request.DeadlineMonotonicMilliseconds) return Outcome("timed_out", "Auditor review reached its deadline.");
        if (!capability.SupportedPurposes.Contains(request.Purpose, StringComparer.Ordinal))
            return Outcome("unavailable", "The auditor does not support this checkpoint; no assessment was returned.");

        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1, request.DeadlineMonotonicMilliseconds - Now)), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var contextJson = CreateContextJson(request);
            if (Encoding.UTF8.GetByteCount(contextJson) > capability.Limits.MaxContextBytes)
                return Outcome("unavailable", "The audit context exceeds the admitted provider byte limit; it was not truncated or invoked.");
            var prepared = await prepare(request, capability, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (prepared is { EncodedInputTokens: > 0 } && prepared.EncodedInputTokens > capability.Limits.MaxContextTokens)
                return Outcome("unavailable", "The measured complete input exceeds the admitted provider token limit; it was not truncated or invoked.");
            ValidatePrepared(request, capability, contextJson, prepared);
            if (CaptureCapability().Sha256() != fingerprint)
                return Outcome("stale", "The admitted auditor capability changed during preparation.");
            if (Now >= request.DeadlineMonotonicMilliseconds)
                return Outcome("timed_out", "Auditor preparation reached its deadline; the provider was not invoked.");
            var state = new AdvisorHostState(prepared.Request.InputRevision, prepared.Request.ContextSha256,
                prepared.Request.Sequence, Now, cancellationToken.IsCancellationRequested);
            // Do not use WaitAsync: a noncooperative provider must finish before its scheduler slot is released.
            var result = await module.InvokeAsync(prepared.Request, state, linked.Token).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                return Outcome("cancelled", "Auditor review was cancelled; no assessment was accepted.");
            if (deadline.IsCancellationRequested || Now >= request.DeadlineMonotonicMilliseconds)
                return Outcome("timed_out", "Auditor review reached its deadline; no late assessment was accepted.");
            if (CaptureCapability().Sha256() != fingerprint)
                return Outcome("stale", "The admitted auditor capability changed while the review was running.");
            ValidateResult(prepared.Request, capability, result);
            if (result.Disposition != "accepted") return Outcome(result.Disposition, Summary(result.Disposition));
            // The native schema reader must validate criterion/question/option-order bindings against
            // this prepared request before interpreting its domain result. It is not a free-text parser.
            return readCriterion(prepared.Request, result) switch
            {
                WorkflowAdvisorCriterion.Met => Outcome("accepted", "The auditor assessed this criterion as met; this remains advisory evidence."),
                WorkflowAdvisorCriterion.Gap => Outcome("gap", "The auditor identified a gap in this criterion; deterministic checks remain authoritative."),
                WorkflowAdvisorCriterion.InsufficientEvidence => Outcome("insufficient_evidence", "The auditor had insufficient evidence to assess this criterion."),
                _ => throw new AuditorContractException("The provider returned an unsupported criterion status.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Outcome("cancelled", "Auditor review was cancelled; no assessment was accepted."); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested || Now >= request.DeadlineMonotonicMilliseconds)
        { return Outcome("timed_out", "Auditor review reached its deadline."); }
        catch (AuditorContractException)
        { return Outcome("rejected", "The auditor returned unbound or invalid advisory data; no assessment was accepted."); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        { return Outcome("failed", "The auditor failed; no assessment was accepted."); }
    }

    private long Now => (long)(time.GetTimestamp() * 1000d / time.TimestampFrequency);

    private AdvisorCapability CaptureCapability()
    {
        var capability = module.Capability;
        capability.Validate();
        if (capability.ModuleId != moduleId || !capability.ModuleKinds.Contains("auditor", StringComparer.Ordinal))
            throw new AuditorContractException("The admitted auditor identity changed.");
        return capability with
        {
            ModuleKinds = Array.AsReadOnly(capability.ModuleKinds.ToArray()),
            RequestSchemaIds = Array.AsReadOnly(capability.RequestSchemaIds.ToArray()),
            ResultSchemaIds = Array.AsReadOnly(capability.ResultSchemaIds.ToArray()),
            SupportedPurposes = Array.AsReadOnly(capability.SupportedPurposes.ToArray()),
            SupportedDecisionTypes = Array.AsReadOnly(capability.SupportedDecisionTypes.ToArray()),
            SupportedDataZones = Array.AsReadOnly(capability.SupportedDataZones.ToArray()),
            ExecutionModes = Array.AsReadOnly(capability.ExecutionModes.ToArray())
        };
    }

    private static void ValidateWorkflowRequest(WorkflowAuditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var identity in new[] { request.RequestId, request.RunId, request.ConversationId, request.Purpose })
            _ = AuditorJson.Identifier(JsonValue.Create(identity), "workflow audit identity");
        _ = AuditorJson.Sha256(JsonValue.Create(request.InputSha256), "workflow input hash");
        _ = AuditorJson.Sha256(JsonValue.Create(request.ProposalSha256), "workflow proposal hash");
        if (request.RunRevision < 1 || request.ConversationRevision < 0 || request.EngineEpoch < 0 ||
            request.CreatedMonotonicMilliseconds < 0 || request.DeadlineMonotonicMilliseconds <= request.CreatedMonotonicMilliseconds ||
            request.Context.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(request.Context.GetRawText()) > WorkflowContract.MaximumStateBytes)
            throw new AuditorContractException("Workflow audit counters, deadline or bounded context are invalid.");
    }

    private static void ValidatePrepared(WorkflowAuditRequest workflow, AdvisorCapability capability,
        string contextJson, WorkflowAdvisorPreparedRequest prepared)
    {
        if (prepared?.Request is not { } request)
            throw new AuditorContractException("The provider did not prepare an advisor request.");
        request.Validate(capability);
        var encodedContextTokens = request.ContextTokens;
        if (prepared.InputProjection is { } projection)
        {
            var packet = AuditorJson.ParseObject(workflow.Context.GetRawText(), "workflow encoded packet");
            if (projection.Json != AuditorJson.Canonical(packet) || projection.Sha256 != AuditorJson.Hash(packet) ||
                projection.Tokens < 1 || projection.Tokens > capability.Limits.MaxContextTokens ||
                Encoding.UTF8.GetByteCount(projection.Json) > capability.Limits.MaxContextBytes)
                throw new AuditorContractException("The encoded input projection must bind the exact canonical packet and its measured token count.");
            encodedContextTokens = projection.Tokens;
        }
        if (request.RequestId != workflow.RequestId || request.IdempotencyKey != workflow.RequestId || request.Attempt != 1 ||
            request.TaskId != workflow.RunId || request.ConversationId != workflow.ConversationId || request.StreamId != workflow.RunId ||
            request.InputRevision != workflow.RunRevision || request.Sequence != workflow.ConversationRevision ||
            request.Purpose != workflow.Purpose || request.ModuleKind != "auditor" ||
            request.CreatedMonoMs != workflow.CreatedMonotonicMilliseconds || request.DeadlineMonoMs != workflow.DeadlineMonotonicMilliseconds ||
            request.MaxResultAgeMs > workflow.DeadlineMonotonicMilliseconds - workflow.CreatedMonotonicMilliseconds ||
            request.ContextSchemaId != "cerebral.workflow-advisor.context.v1" || request.ContextJson != contextJson ||
            request.ContextTokens < 1 || prepared.EncodedInputTokens < encodedContextTokens ||
            prepared.EncodedInputTokens > capability.Limits.MaxContextTokens)
            throw new AuditorContractException("The prepared request changed workflow identity, hashes, deadline or measured token bounds.");
    }

    private static void ValidateResult(AdvisorRequest request, AdvisorCapability capability, AdvisorResult result)
    {
        if (result is null || result.SchemaVersion != AdvisorContract.Result || result.RequestId != request.RequestId ||
            result.IdempotencyKey != request.IdempotencyKey || result.Attempt != request.Attempt ||
            result.ModuleId != capability.ModuleId || result.InstanceGeneration != capability.InstanceGeneration ||
            result.CapabilitySha256 != request.CapabilitySha256 || !result.AdvisoryOnly || result.DirectActionAllowed ||
            !AdvisorContract.DeliveryDispositions.Contains(result.Disposition))
            throw new AuditorContractException("Advisor result identity, capability or advisory authority does not match its request.");
        _ = AuditorJson.Identifier(JsonValue.Create(result.ReasonCode), "advisor reason code");
        if (result.DomainResultJson is null)
        {
            if (result.Disposition == "accepted" || result.DomainResultSchemaId is not null || result.DomainResultSha256 is not null)
                throw new AuditorContractException("Accepted assessments require a bound domain result.");
            return;
        }
        if (result.DomainResultSchemaId is null || !capability.ResultSchemaIds.Contains(result.DomainResultSchemaId, StringComparer.Ordinal) ||
            Encoding.UTF8.GetByteCount(result.DomainResultJson) > WorkflowContract.MaximumStateBytes)
            throw new AuditorContractException("Advisor domain result is unadmitted or exceeds the workflow bound.");
        var domain = AuditorJson.ParseObject(result.DomainResultJson, "advisor domain result");
        if (AuditorJson.Canonical(domain) != result.DomainResultJson || AuditorJson.Hash(domain) != result.DomainResultSha256)
            throw new AuditorContractException("Advisor domain result canonical bytes or digest do not match.");
    }

    private static string Summary(string disposition) => disposition switch
    {
        "unavailable" => "The auditor was unavailable; no assessment was returned.",
        "stale" => "The auditor assessment was stale; it was not accepted.",
        "timed_out" => "The auditor reached its deadline; no assessment was accepted.",
        "cancelled" => "The auditor was cancelled; no assessment was accepted.",
        "rejected" => "The auditor rejected the request; no assessment was accepted.",
        _ => "The auditor failed; no assessment was accepted."
    };
}
