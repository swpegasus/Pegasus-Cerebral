using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Engine.Workflows;

internal sealed record WorkflowAuditCommit(WorkflowAuditRequest Request, WorkflowAuditResult Result, bool Hold, Func<bool> Fresh);

/// <summary>Host-owned binding, deadline and advisory-only checks. Final freshness is enforced by the journal transaction.</summary>
public sealed class WorkflowAuditPipeline
{
    private readonly IReadOnlyDictionary<string, (IWorkflowAuditAdapter Adapter, WorkflowAuditDescriptor Binding)> adapters;
    public WorkflowAuditPipeline(IEnumerable<IWorkflowAuditAdapter>? adapters = null)
    {
        var admitted = new Dictionary<string, (IWorkflowAuditAdapter Adapter, WorkflowAuditDescriptor Binding)>(StringComparer.Ordinal);
        foreach (var adapter in adapters ?? [])
        {
            var descriptor = adapter?.Descriptor ?? throw new ArgumentException("Audit adapter has no descriptor.");
            if (string.IsNullOrWhiteSpace(descriptor.Id) || descriptor.Id.Length > 256 ||
                descriptor.Id.Any(char.IsControl) || string.IsNullOrWhiteSpace(descriptor.PackageId) || descriptor.PackageId.Length > 128 ||
                descriptor.PackageId.Any(char.IsControl) || !Version.TryParse(descriptor.Version, out _) ||
                descriptor.Generation < 1 || descriptor.Fingerprint is not { Length: 64 } fingerprint || !fingerprint.All(Uri.IsHexDigit) ||
                descriptor.Purposes is not { Count: >= 1 and <= 8 } purposes ||
                purposes.Any(purpose => string.IsNullOrWhiteSpace(purpose) || purpose.Length > 128 || purpose.Any(char.IsControl)) ||
                purposes.Distinct(StringComparer.Ordinal).Count() != purposes.Count || !admitted.TryAdd(descriptor.Id,
                    (adapter!, descriptor with { Purposes = descriptor.Purposes.ToArray() })))
                throw new ArgumentException("Audit adapter binding is invalid or duplicated.");
        }
        this.adapters = admitted;
    }
    internal static long Now => (long)(Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency);
    internal static string Hash(JsonElement data) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.GetRawText()))).ToLowerInvariant();

    private static bool IsCurrent(IWorkflowAuditAdapter adapter, WorkflowAuditDescriptor binding)
    {
        try
        {
            var live = adapter.Descriptor;
            return live is not null && live.Id == binding.Id && live.PackageId == binding.PackageId && live.Version == binding.Version &&
                live.Generation == binding.Generation && live.Fingerprint == binding.Fingerprint && live.Purposes is not null &&
                live.Purposes.SequenceEqual(binding.Purposes, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        { return false; }
    }

    internal async Task<WorkflowAuditCommit> ReviewAsync(WorkflowRun run, WorkflowStep step, long sequence,
        long epoch, WorkflowAuditPolicy policy, string purpose, CancellationToken token)
    {
        if (policy.TimeoutMilliseconds is < 1 or > 30_000) throw new ArgumentException("Audit timeout must be 1–30000 milliseconds.");
        var witness = JsonSerializer.SerializeToElement(new { run.Submission, run.State, run.Signal, run.Steering });
        var proposal = JsonSerializer.SerializeToElement(step.Next, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var created = Now;
        string Clip(string? text, int size)
        {
            if (text is null) return "";
            var length = Math.Min(text.Length, size);
            if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
            return text[..length];
        }
        var context = JsonSerializer.SerializeToElement(new
        {
            requirement = Clip(run.Submission.UserText, 1600),
            acceptanceCriterion = purpose == "coordinator_decision_check"
                ? "The bounded assignments cover the accepted task and preserve each agent's scope. Identify missing evidence."
                : "The completion claim is supported by current execution evidence; distinguish unresolved gaps from established results.",
            proposedAction = step.Next switch
            {
                InvokeWorkflowCommand command => Clip(command.Arguments.GetRawText(), 6000),
                CompleteWorkflow complete => Clip(complete.Result.GetRawText(), 4000),
                _ => ""
            },
            observation = Clip(run.Signal.Data.GetRawText(), 6000),
            evidenceRunId = run.Id, evidenceRevision = run.Revision, run.Submission.ProfileId,
            knownMissingEvidence = "This packet is bounded; omitted transcripts and receipts must not be inferred."
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var request = new WorkflowAuditRequest(Guid.NewGuid().ToString("N"), run.Id, run.Submission.ConversationId,
            run.Revision, sequence, epoch, purpose, Hash(witness), Hash(proposal), context, created, checked(created + policy.TimeoutMilliseconds));
        WorkflowAuditResult Outcome(string disposition, string summary, WorkflowAuditDescriptor? descriptor = null) =>
            new(request.RequestId, descriptor?.Id ?? policy.AdapterId ?? "unconfigured", descriptor?.Generation ?? 0,
                descriptor?.Fingerprint ?? new string('0', 64), request.InputSha256, request.ProposalSha256, disposition, summary);
        WorkflowAuditResult result;
        Func<bool> fresh = () => true;
        if (policy.AdapterId is null || !adapters.TryGetValue(policy.AdapterId, out var selected))
            result = Outcome("unavailable", "No admitted auditor is selected; deterministic checks remain authoritative.");
        else
        {
            var adapter = selected.Adapter;
            var binding = selected.Binding;
            if (!IsCurrent(adapter, binding))
                result = Outcome("unavailable", "The auditor binding changed; readmit its capability before use.", binding);
            else if (!binding.Purposes.Contains(purpose, StringComparer.Ordinal))
                result = Outcome("unavailable", "The selected auditor does not support this checkpoint.", binding);
            else
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(policy.TimeoutMilliseconds));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
                try
                {
                    // Keep the scheduler slot until a noncooperative provider exits; timeout cannot create hidden overlapping inference.
                    result = await adapter.ReviewAsync(request, linked.Token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (timeout.IsCancellationRequested || Now > request.DeadlineMonotonicMilliseconds)
                        result = Outcome("timed_out", "Auditor exceeded this checkpoint's deadline.", binding);
                    else if (!IsCurrent(adapter, binding) ||
                        result is null || result.RequestId != request.RequestId || result.AdapterId != binding.Id ||
                        result.Generation != binding.Generation || result.Fingerprint != binding.Fingerprint ||
                        result.InputSha256 != request.InputSha256 || result.ProposalSha256 != request.ProposalSha256 ||
                        !result.AdvisoryOnly || result.DirectActionAllowed || result.Summary is null || result.Summary.Length > 2048 ||
                        result.Disposition is not ("accepted" or "unavailable" or "insufficient_evidence" or "gap" or "rejected" or "failed" or "stale" or "timed_out" or "cancelled"))
                        result = Outcome("rejected", "Auditor returned stale, unbound or invalid advisory data.", binding);
                    else if (result.Disposition is "accepted" or "gap" or "insufficient_evidence")
                        fresh = () =>
                        {
                            return !token.IsCancellationRequested && Now <= request.DeadlineMonotonicMilliseconds &&
                                IsCurrent(adapter, binding);
                        };
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { result = Outcome("timed_out", "Auditor cancelled at its deadline.", binding); }
                catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException or OperationCanceledException))
                { result = Outcome("failed", "Auditor failed; no assessment was accepted.", binding); }
            }
        }
        return new(request, result, policy.Required && result.Disposition != "accepted", fresh);
    }
}
