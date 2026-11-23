using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Auditing;
using Cerebral.Contracts.Workflows;

internal static class WorkflowAdvisorAdapterTests
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new Exception("FAIL: workflow advisor " + label);
            checks++; Console.WriteLine("PASS: workflow advisor " + label);
        }
        static WorkflowAuditRequest Packet() => new("review.1", "run.1", "conversation.1", 4, 9, 7,
            "coordinator_decision_check", new string('a', 64), new string('b', 64),
            JsonSerializer.SerializeToElement(new { requirement = "café criterion", observation = "fixture only" }), 1_000, 2_000);
        static AdvisorCapability Capability() => new(AdvisorContract.Capability, "fixture.native-advisor", "1.0.0", 3,
            ["auditor"], 1, 1, "fixture.only", new string('f', 64), "fixture.in-process", "fixture.no-model",
            ["fixture.native-request.v1"], ["fixture.native-result.v1"], ["coordinator_decision_check", "component_evidence_check"],
            ["categorical"], ["private"], ["shadow"], new(16_384, 512, 4, 1, 3, 1), new(0, 0, 0, 1), "deterministic");
        static AdvisorResult Result(AdvisorRequest request, AdvisorCapability capability, string criterion = "met", string disposition = "accepted")
        {
            var json = disposition == "accepted" ? "{\"criterion_id\":\"fixture_criterion\",\"criterion_status\":\"" + criterion +
                "\",\"decision_schema_sha256\":\"" + request.DecisionSchemaSha256 + "\"}" : null;
            return new(AdvisorContract.Result, request.RequestId, request.IdempotencyKey, request.Attempt, capability.ModuleId,
                capability.InstanceGeneration, capability.Sha256(), disposition, "fixture_result",
                json is null ? null : "fixture.native-result.v1", json, json is null ? null : Hash(json), true, false);
        }
        static WorkflowAdvisorPreparedRequest Prepare(WorkflowAuditRequest workflow, AdvisorCapability capability)
        {
            var context = WorkflowAdvisorAdapter.CreateContextJson(workflow);
            return new(new(AdvisorContract.Request, workflow.RequestId, workflow.RequestId, 1,
                capability.ModuleId, capability.Sha256(), workflow.RunId, workflow.ConversationId, workflow.RunId,
                workflow.ConversationRevision, workflow.RunRevision, Hash(context), new string('c', 64),
                "scope.fixture", "private", "low", workflow.Purpose, "auditor", "shadow",
                workflow.CreatedMonotonicMilliseconds, workflow.DeadlineMonotonicMilliseconds, 500, "cancel.fixture",
                "cerebral.workflow-advisor.context.v1", context, Encoding.UTF8.GetByteCount(context), 64,
                [], [], "fixture.native-request.v1", "{\"criterion_id\":\"fixture_criterion\",\"fixture\":true}"), 128);
        }
        static WorkflowAdvisorCriterion ReadCriterion(AdvisorRequest request, AdvisorResult result)
        {
            using var json = JsonDocument.Parse(result.DomainResultJson!);
            using var input = JsonDocument.Parse(request.DomainRequestJson);
            if (json.RootElement.GetProperty("criterion_id").GetString() != input.RootElement.GetProperty("criterion_id").GetString() ||
                json.RootElement.GetProperty("decision_schema_sha256").GetString() != request.DecisionSchemaSha256)
                throw new AuditorContractException("Fixture native criterion or ordered-decision binding mismatch.");
            return json.RootElement.GetProperty("criterion_status").GetString() switch
            {
                "met" => WorkflowAdvisorCriterion.Met,
                "gap" => WorkflowAdvisorCriterion.Gap,
                "insufficient_evidence" => WorkflowAdvisorCriterion.InsufficientEvidence,
                _ => (WorkflowAdvisorCriterion)99
            };
        }
        var clock = new FixtureClock();
        var module = new FixtureModule(Capability());
        module.Evaluate = (request, _, _) => ValueTask.FromResult(Result(request, module.Capability));
        var preparedCalls = 0;
        var interpretedCalls = 0;
        Func<WorkflowAuditRequest, AdvisorCapability, WorkflowAdvisorPreparedRequest> prepare = Prepare;
        var adapter = new WorkflowAdvisorAdapter(module, "fixture.package", (workflow, capability, token) =>
        {
            preparedCalls++; token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(prepare(workflow, capability));
        }, (request, result) => { interpretedCalls++; return ReadCriterion(request, result); }, clock);
        var packet = Packet();
        var descriptor = adapter.Descriptor;
        Check(descriptor.Fingerprint == module.Capability.Sha256() && descriptor.Fingerprint != module.Capability.ImplementationSha256 &&
            descriptor.Generation == 3 && descriptor.Purposes.Count == 2,
            "descriptor binds complete capability digest and lifecycle generation");
        var accepted = await adapter.ReviewAsync(packet, CancellationToken.None);
        Check(accepted is { Disposition: "accepted", AdvisoryOnly: true, DirectActionAllowed: false } &&
            accepted.InputSha256 == packet.InputSha256 && accepted.ProposalSha256 == packet.ProposalSha256 &&
            accepted.Generation == descriptor.Generation && accepted.Fingerprint == descriptor.Fingerprint,
            "native deterministic assessment preserves workflow hashes and advisory-only identity");
        Check(module.LastRequest!.DecisionSchemaSha256 != packet.ProposalSha256 &&
            module.LastRequest.ContextSha256 == Hash(WorkflowAdvisorAdapter.CreateContextJson(packet)) &&
            module.LastState is { CurrentRevision: 4, CurrentSequence: 9, NowMonoMs: 1_000 },
            "bounded projection hash, witness hash and native decision-schema hash remain distinct");
        using (var context = JsonDocument.Parse(module.LastRequest.ContextJson))
            Check(context.RootElement.GetProperty("input_sha256").GetString() == packet.InputSha256 &&
                context.RootElement.GetProperty("proposal_sha256").GetString() == packet.ProposalSha256 &&
                context.RootElement.GetProperty("engine_epoch").GetInt64() == packet.EngineEpoch &&
                context.RootElement.GetProperty("packet").GetProperty("requirement").GetString() == "café criterion",
                "canonical context binds exact witness, proposal, engine epoch and Unicode projection");
        foreach (var (criterion, expected) in new[] { ("gap", "gap"), ("insufficient_evidence", "insufficient_evidence") })
        {
            module.Evaluate = (request, _, _) => ValueTask.FromResult(Result(request, module.Capability, criterion));
            Check((await adapter.ReviewAsync(packet, CancellationToken.None)).Disposition == expected,
                "semantic " + expected + " is distinct from provider unavailability");
        }
        foreach (var disposition in new[] { "unavailable", "stale", "timed_out", "cancelled", "rejected", "failed" })
        {
            var before = interpretedCalls;
            module.Evaluate = (request, _, _) => ValueTask.FromResult(Result(request, module.Capability, disposition: disposition));
            Check((await adapter.ReviewAsync(packet, CancellationToken.None)).Disposition == disposition && interpretedCalls == before,
                disposition + " preserves terminal provenance without fabricating an assessment");
        }
        module.Evaluate = (request, _, _) => ValueTask.FromResult(Result(request, module.Capability));
        async Task RejectPreparation(Func<WorkflowAdvisorPreparedRequest, WorkflowAdvisorPreparedRequest> change, string label)
        {
            prepare = (workflow, capability) => change(Prepare(workflow, capability));
            var before = module.Calls;
            Check((await adapter.ReviewAsync(packet, CancellationToken.None)).Disposition == "rejected" && module.Calls == before, label);
            prepare = Prepare;
        }
        await RejectPreparation(value => value with { Request = value.Request with { TaskId = "other.run" } }, "foreign prepared task is rejected before invocation");
        await RejectPreparation(value => value with { Request = value.Request with { DeadlineMonoMs = 3_000 } }, "prepared deadline cannot be extended");
        await RejectPreparation(value => value with { Request = value.Request with { CapabilitySha256 = new string('0', 64) } }, "prepared capability digest cannot be substituted");
        await RejectPreparation(value => value with { EncodedInputTokens = 0 }, "missing full-token preflight is rejected");
        await RejectPreparation(value => value with { EncodedInputTokens = 63 }, "full encoded count cannot undercount the canonical context");
        var exactPacket = "{\"observation\":\"fixture only\",\"requirement\":\"café criterion\"}";
        prepare = (workflow, capability) => Prepare(workflow, capability) with
        { EncodedInputTokens = 40, InputProjection = new(exactPacket, Hash(exactPacket), 32) };
        Check((await adapter.ReviewAsync(packet, CancellationToken.None)).Disposition == "accepted",
            "exact encoded packet can be smaller than the separately measured host metadata envelope");
        prepare = Prepare;
        await RejectPreparation(value => value with { EncodedInputTokens = 40,
            InputProjection = new(exactPacket, new string('0', 64), 32) }, "encoded packet hash cannot be substituted");
        await RejectPreparation(value => value with { EncodedInputTokens = 40,
            InputProjection = new("{}", Hash("{}"), 2) }, "encoded projection cannot omit criterion packet content");
        await RejectPreparation(value => value with { EncodedInputTokens = 40,
            InputProjection = new(exactPacket, Hash(exactPacket), 0) }, "encoded packet requires a measured tokenizer count");
        await RejectPreparation(value => value with { EncodedInputTokens = 31,
            InputProjection = new(exactPacket, Hash(exactPacket), 32) }, "full input cannot undercount the exact encoded packet");
        await RejectPreparation(value => value with { Request = value.Request with { ContextTokens = 0 } }, "nonempty context requires a measured context-token count");
        await RejectPreparation(value =>
        {
            var json = value.Request.ContextJson.Replace(packet.InputSha256, new string('d', 64), StringComparison.Ordinal);
            return value with { Request = value.Request with { ContextJson = json, ContextSha256 = Hash(json) } };
        }, "canonical but substituted workflow witness is rejected");
        prepare = (workflow, capability) => Prepare(workflow, capability) with { EncodedInputTokens = 513 };
        var calls = module.Calls;
        var overflow = await adapter.ReviewAsync(packet, CancellationToken.None);
        Check(overflow.Disposition == "unavailable" && overflow.Summary.Contains("token limit", StringComparison.Ordinal) && module.Calls == calls,
            "measured full-input overflow is explicit and never invokes or truncates the provider");
        prepare = Prepare;
        module.Capability = module.Capability with { Limits = module.Capability.Limits with { MaxContextBytes = 16 } };
        var preparations = preparedCalls;
        Check((await adapter.ReviewAsync(packet, CancellationToken.None)).Disposition == "unavailable" && preparedCalls == preparations && module.Calls == calls,
            "context-byte overflow is rejected before token preparation or provider invocation");
        module.Capability = Capability();
        async Task RejectOutput(Func<AdvisorResult, AdvisorResult> change, string label)
        {
            module.Evaluate = (request, _, _) => ValueTask.FromResult(change(Result(request, module.Capability)));
            Check((await adapter.ReviewAsync(packet, CancellationToken.None)).Disposition == "rejected", label);
        }
        await RejectOutput(value => value with { RequestId = "other.review" }, "foreign result request identity fails closed");
        await RejectOutput(value => value with { IdempotencyKey = "other.idempotency" }, "foreign result idempotency key fails closed");
        await RejectOutput(value => value with { InstanceGeneration = 4 }, "foreign result lifecycle generation fails closed");
        await RejectOutput(value => value with { DirectActionAllowed = true }, "provider cannot grant direct action authority");
        await RejectOutput(value => value with { DomainResultSha256 = new string('0', 64) }, "domain output digest is recomputed");
        await RejectOutput(value => value with { DomainResultSchemaId = "other.domain" }, "unadmitted domain schema is rejected");
        await RejectOutput(value =>
        {
            var json = value.DomainResultJson!.Replace("fixture_criterion", "other_criterion", StringComparison.Ordinal);
            return value with { DomainResultJson = json, DomainResultSha256 = Hash(json) };
        }, "typed native reader rejects a correctly hashed result for another criterion");
        await RejectOutput(value =>
        {
            var json = value.DomainResultJson!.Replace(new string('c', 64), new string('e', 64), StringComparison.Ordinal);
            return value with { DomainResultJson = json, DomainResultSha256 = Hash(json) };
        }, "typed native reader rejects a correctly hashed result for another ordered decision schema");
        await RejectOutput(value => value with { DomainResultJson = null, DomainResultSchemaId = null, DomainResultSha256 = null }, "accepted disposition requires an actual bound assessment");
        module.Evaluate = (request, _, _) => ValueTask.FromResult(Result(request, module.Capability, "unknown"));
        Check((await adapter.ReviewAsync(packet, CancellationToken.None)).Disposition == "rejected", "unknown native criterion cannot become accepted");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); preparations = preparedCalls; calls = module.Calls;
            Check((await adapter.ReviewAsync(packet, cancelled.Token)).Disposition == "cancelled" && preparations == preparedCalls && calls == module.Calls,
                "initial cancellation never prepares or invokes the provider");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            module.Evaluate = (request, _, _) => { cancellation.Cancel(); return ValueTask.FromResult(Result(request, module.Capability)); };
            Check((await adapter.ReviewAsync(packet, cancellation.Token)).Disposition == "cancelled", "cancellation wins over a late provider assessment");
        }
        clock.Timestamp = 2_000; calls = module.Calls;
        Check((await adapter.ReviewAsync(packet, CancellationToken.None)).Disposition == "timed_out" && calls == module.Calls,
            "expired monotonic deadline never invokes the provider");
        clock.Timestamp = 1_000;
        prepare = (workflow, capability) => { clock.Timestamp = 2_000; return Prepare(workflow, capability); };
        Check((await adapter.ReviewAsync(packet, CancellationToken.None)).Disposition == "timed_out" && calls == module.Calls,
            "deadline reached in token preparation prevents model invocation");
        clock.Timestamp = 1_000; prepare = Prepare;
        module.Evaluate = (request, _, _) => { clock.Timestamp = 2_000; return ValueTask.FromResult(Result(request, module.Capability)); };
        Check((await adapter.ReviewAsync(packet, CancellationToken.None)).Disposition == "timed_out", "late provider output is not accepted after deadline");
        clock.Timestamp = 1_000;
        prepare = (workflow, capability) =>
        {
            var value = Prepare(workflow, capability); module.Capability = capability with { InstanceGeneration = capability.InstanceGeneration + 1 }; return value;
        };
        calls = module.Calls;
        Check((await adapter.ReviewAsync(packet, CancellationToken.None)).Disposition == "stale" && calls == module.Calls,
            "provider replacement during preparation prevents invocation");
        prepare = Prepare; module.Capability = Capability();
        module.Evaluate = (request, _, _) =>
        {
            var result = Result(request, module.Capability); module.Capability = module.Capability with { InstanceGeneration = 4 }; return ValueTask.FromResult(result);
        };
        Check((await adapter.ReviewAsync(packet, CancellationToken.None)).Disposition == "stale", "provider replacement during invocation invalidates output");
        Check(adapter.Descriptor.Generation == 4 && adapter.Descriptor.Fingerprint != descriptor.Fingerprint,
            "live descriptor exposes provider replacement to the host conditional-commit gate");
        module.Capability = Capability();
        module.Evaluate = (_, _, _) => throw new InvalidOperationException("fixture failure detail must not appear in summaries");
        var failed = await adapter.ReviewAsync(packet, CancellationToken.None);
        Check(failed.Disposition == "failed" && !failed.Summary.Contains("fixture failure detail", StringComparison.Ordinal),
            "ordinary provider failures remain visible without exposing raw exception content");
        var pending = new TaskCompletionSource<AdvisorResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        module.Evaluate = (_, _, _) => new(pending.Task);
        var running = adapter.ReviewAsync(packet, CancellationToken.None);
        clock.Timestamp = 2_000;
        Check(!running.IsCompleted, "noncooperative inference remains owned until provider returns");
        pending.SetResult(Result(module.LastRequest!, module.Capability));
        Check((await running).Disposition == "timed_out", "noncooperative late result is discarded after owned completion");
        Check(typeof(WorkflowAdvisorAdapter).Assembly.GetReferencedAssemblies().All(reference =>
            !reference.Name!.StartsWith("Cerebral.", StringComparison.Ordinal) || reference.Name == "Cerebral.Contracts"),
            "bridge uses only shared contracts and has no Worker, Engine or STRATA runtime dependency");
        Console.WriteLine($"Workflow native advisor deterministic fixture checks passed: {checks}; no model, tokenizer, inference or runtime admission tested.");
        return checks;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private sealed class FixtureClock : TimeProvider
    {
        public long Timestamp { get; set; } = 1_000;
        public override long GetTimestamp() => Timestamp;
        public override long TimestampFrequency => 1_000;
    }
    private sealed class FixtureModule(AdvisorCapability capability) : IAdvisorModule
    {
        public AdvisorCapability Capability { get; set; } = capability;
        public Func<AdvisorRequest, AdvisorHostState, CancellationToken, ValueTask<AdvisorResult>> Evaluate { get; set; } = null!;
        public int Calls { get; private set; }
        public AdvisorRequest? LastRequest { get; private set; }
        public AdvisorHostState? LastState { get; private set; }
        public ValueTask<AdvisorResult> InvokeAsync(AdvisorRequest request, AdvisorHostState state, CancellationToken token)
        {
            Calls++; LastRequest = request; LastState = state; return Evaluate(request, state, token);
        }
    }
}
