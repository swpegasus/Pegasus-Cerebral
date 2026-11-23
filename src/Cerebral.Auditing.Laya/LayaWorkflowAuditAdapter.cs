using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Auditing.Laya;

/// <summary>Seals only explicitly supplied criterion evidence. Omitted material is never inferred.</summary>
internal sealed class LayaWorkflowAuditAdapter(LayaStockAdvisorModule module) : IWorkflowAuditAdapter
{
    public WorkflowAuditDescriptor Descriptor => new(LayaStockAdvisorModule.AdapterId, LayaStockAdvisorModule.PackageId,
        module.Capability.ModuleVersion, module.Capability.InstanceGeneration, module.Capability.Sha256(), module.Capability.SupportedPurposes);
    public async Task<WorkflowAuditResult> ReviewAsync(WorkflowAuditRequest original, CancellationToken token)
    {
        var descriptor = Descriptor;
        WorkflowAuditResult Outcome(string disposition, string summary) => new(original.RequestId, descriptor.Id, descriptor.Generation,
            descriptor.Fingerprint, original.InputSha256, original.ProposalSha256, disposition, summary, true, false);
        try
        {
            _ = WorkflowAdvisorAdapter.CreateContextJson(original); // Validate the complete host binding first.
            if (token.IsCancellationRequested) return Outcome("cancelled", "Native Laya review was cancelled before preparation.");
            if (LayaStockAdvisorModule.Now >= original.DeadlineMonotonicMilliseconds)
                return Outcome("timed_out", "Native Laya review reached its host deadline.");
            if (!descriptor.Purposes.Contains(original.Purpose, StringComparer.Ordinal))
                return Outcome("unavailable", "Native Laya does not support this workflow checkpoint.");
            var originalContextHash = LayaProtocol.Hash(original.Context.GetRawText());
            var packet = new Dictionary<string, object?>
            {
                ["schema_version"] = "cerebral.laya.criterion-projection.v1",
                ["omitted_evidence"] = "Only the supplied criterion and observations are assessed. Omitted conversations, receipts and files are not inferred."
            };
            foreach (var name in new[] { "acceptanceCriterion", "requirement", "proposedAction", "observation",
                "evidenceRunId", "evidenceRevision", "profileId", "knownMissingEvidence" })
                if (original.Context.TryGetProperty(name, out var value)) packet[name] = value.Clone();
            if (!original.Context.TryGetProperty("acceptanceCriterion", out var criterion) ||
                criterion.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(criterion.GetString()))
                return Outcome("insufficient_evidence", "No explicit acceptance criterion was supplied; no criterion was inferred.");
            var projection = LayaProtocol.Canonical(packet);
            if (Encoding.UTF8.GetByteCount(projection) > 32_768)
                return Outcome("unavailable", module.RecordRejection(original.RequestId, new("projection-byte-limit")).Summary());
            using var projectedDocument = LayaProtocol.Parse(projection);
            var workflow = original with { Context = projectedDocument.RootElement.Clone() };
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1,
                original.DeadlineMonotonicMilliseconds - LayaStockAdvisorModule.Now)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            var prepared = await module.PrepareAsync(workflow, originalContextHash, linked.Token).ConfigureAwait(false);
            LayaRejectionDiagnostic? invocationRejection = null;
            var bridge = new WorkflowAdvisorAdapter(new Invocation(module, diagnostic => invocationRejection = diagnostic), LayaStockAdvisorModule.PackageId,
                (request, capability, cancellation) =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (request.RequestId != workflow.RequestId || WorkflowAdvisorAdapter.CreateContextJson(request) != prepared.Request.ContextJson ||
                        capability.Sha256() != prepared.Request.CapabilitySha256)
                        throw new AuditorContractException("Laya preparation does not match its sealed workflow request.");
                    return ValueTask.FromResult(prepared);
                }, LayaStockAdvisorModule.ReadCriterion);
            var outcome = await bridge.ReviewAsync(workflow, linked.Token).ConfigureAwait(false);
            return outcome.Disposition == "unavailable" && invocationRejection is not null
                ? outcome with { Summary = invocationRejection.Summary() } : outcome;
        }
        catch (OperationCanceledException)
        { return Outcome(token.IsCancellationRequested ? "cancelled" : "timed_out", "Native Laya review ended at cancellation or the host deadline; late assessment was discarded."); }
        catch (LayaRejectedException rejection)
        { return Outcome("unavailable", module.RecordRejection(original.RequestId, rejection).Summary()); }
        catch (IOException)
        { return Outcome("unavailable", "The native Laya CPU provider is unavailable; no assessment was accepted."); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        { return Outcome("rejected", "Native Laya returned invalid or unbound advisory data; no assessment was accepted."); }
    }
    // Per-call capture keeps a workflow summary bound to its own invocation under concurrent reviews;
    // owner status can independently retain the last validated rejection without serving as evidence.
    private sealed class Invocation(LayaStockAdvisorModule module, Action<LayaRejectionDiagnostic> rejected) : IAdvisorModule
    {
        public AdvisorCapability Capability => module.Capability;
        public ValueTask<AdvisorResult> InvokeAsync(AdvisorRequest request, AdvisorHostState state, CancellationToken token)
            => module.InvokeWithDiagnosticsAsync(request, state, rejected, token);
    }
}
