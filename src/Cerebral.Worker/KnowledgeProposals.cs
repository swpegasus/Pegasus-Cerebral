using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Uses existing questions and the task queue to approve an exact knowledge write, without a second scheduler.</summary>
public sealed class KnowledgeProposals(StateStore store, Configuration configuration, IToolDispatcher tools, EventHub events)
{
    public const string Approve = "Approve this knowledge change";
    public const string Reject = "Reject this change";
    private readonly ProjectKnowledge projects = new(store, configuration, events);
    public KnowledgeProposal Get(string id) => store.Get<KnowledgeProposal>("knowledge-proposal", id) ?? throw new ArgumentException("Knowledge proposal not found.");
    public List<KnowledgeProposal> List(string sessionId) => store.List<KnowledgeProposal>("knowledge-proposal")
        .Where(x => x.SessionId == sessionId).OrderByDescending(x => x.CreatedAt).Take(50).ToList();

    public ToolResult? ProposeIfRequired(AgentJob job, AgentProfile profile, string toolName, JsonElement arguments)
    {
        var session = store.GetSessionMetadata(job.SessionId)!;
        var context = projects.ContextFor(session, profile, job.Id);
        var access = context.Cortex;
        var pieces = toolName.Split('.', 3);
        if (access is null || !access.RequireKnowledgeApproval || pieces.Length != 3 || pieces[0] != "mcp" ||
            pieces[1] != access.ServerId || !CortexOperationPolicy.IsDurableWrite(pieces[2], arguments)) return null;
        // Missing grants go through the normal dispatcher denial; asking cannot grant new capabilities.
        if ((!profile.AllowedTools.Contains("*") && !profile.AllowedTools.Contains(toolName)) ||
            !profile.Capabilities.Contains("mcp." + pieces[1]) || !profile.Capabilities.Contains("cortex.write")) return null;
        if (arguments.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(arguments.GetRawText()) > 16 * 1024)
            return RecordProposalError(job, "Knowledge proposal must be an object of at most 16 KiB; narrow the proposed change.");
        string providerFingerprint;
        try
        {
            CortexOperationPolicy.ValidateExplicitMutation(toolName, arguments, job.SessionId);
            var host = configuration.Get();
            if (access.WriteNamespace is null || arguments.GetProperty("namespace").GetString() != access.WriteNamespace ||
                !host.CortexWriteNamespaces.Contains(access.WriteNamespace, StringComparer.Ordinal))
                throw new UnauthorizedAccessException("The proposed knowledge namespace is outside this project's current write grant.");
            var server = host.McpServers.SingleOrDefault(x => x.Enabled && x.Id == access.ServerId)
                ?? throw new InvalidOperationException("The selected Cortex connection is not enabled.");
            providerFingerprint = CortexOperationPolicy.ProviderFingerprint(server);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        { return RecordProposalError(job, ex.Message); }
        var fingerprint = ExecutionReliability.Fingerprint(toolName, arguments);
        return store.Locked(() =>
        {
            var liveJob = store.Get<AgentJob>("job", job.Id)!;
            if (liveJob.CancelRequested) throw new OperationCanceledException("Task stopped before knowledge proposal.");
            var existing = List(job.SessionId).FirstOrDefault(x => x.JobId == job.Id && x.Fingerprint == fingerprint);
            if (existing is not null)
            {
                if (existing.Status == "pending" && store.Get<UserQuestion>("question", existing.QuestionId)?.Answer is null)
                { job.Status = "waiting-human"; job.WaitingReason = "Waiting for review of the proposed knowledge change."; }
                var receipt = new ToolResult(true, $"Knowledge proposal {existing.Id} is {existing.Status}. This receipt did not execute a Cortex write. Inspect its retained question/result; do not repeat the same proposal.");
                Append(job, receipt.Content); return receipt;
            }
            var settings = projects.Get(session.ProjectId!);
            var proposal = new KnowledgeProposal { SessionId = job.SessionId, JobId = job.Id, ProjectId = session.ProjectId!,
                ProjectRevision = settings.Revision, ProviderFingerprint = providerFingerprint, ToolName = toolName, Arguments = arguments.Clone(), Fingerprint = fingerprint };
            var json = JsonSerializer.Serialize(arguments, CerebralJson.Options);
            var question = new UserQuestion
            {
                SessionId = job.SessionId, JobId = job.Id, KnowledgeProposalId = proposal.Id, Required = true,
                Prompt = "Approve this change to project knowledge?",
                Reason = "This updates retained Cortex knowledge. Approval applies only to the exact proposal below. Choose Approve to apply it, Reject to leave knowledge unchanged, or enter a correction to request a revised proposal.\n\nTool: " + toolName +
                    "\n\n```json\n" + json + "\n```",
                Choices = [Approve, Reject], CanAssume = false
            };
            proposal.QuestionId = question.Id;
            store.Put("knowledge-proposal", proposal.Id, proposal); store.Put("question", question.Id, question);
            job.Status = "waiting-human"; job.WaitingReason = "Waiting for approval of a proposed knowledge change.";
            // The scheduler will persist the execution snapshot after the engine yields.
            liveJob.Status = job.Status; liveJob.WaitingReason = job.WaitingReason; store.Put("job", job.Id, liveJob);
            var result = new ToolResult(true, $"Knowledge proposal {proposal.Id} is waiting for the user. No Cortex write has executed.");
            Append(job, result.Content); events.Publish("question.updated", job.Id, question); return result;
        });
    }

    // Called once while this service owns its state lock, before accepting new work.
    public void RecoverInterrupted()
    {
        foreach (var proposal in store.List<KnowledgeProposal>("knowledge-proposal").Where(x => x.Status == "attempting"))
        {
            var job = store.Get<AgentJob>("job", proposal.JobId);
            if (job is not null) Complete(proposal, job, new(false, "The service stopped during an approved knowledge write. Its effects are uncertain; inspect Cortex before retrying."), "uncertain");
        }
    }

    public async Task<bool> ProcessPendingAsync(AgentJob job, AgentProfile profile, CancellationToken token)
    {
        foreach (var proposal in List(job.SessionId).Where(x => x.JobId == job.Id && x.Status is "pending" or "attempting"))
        {
            token.ThrowIfCancellationRequested();
            if (store.Get<AgentJob>("job", job.Id)?.CancelRequested != false) throw new OperationCanceledException(token);
            if (proposal.Status == "attempting")
            {
                Complete(proposal, job, new(false, "The prior approved write was interrupted. Its effects are uncertain; inspect Cortex before proposing a retry."), "uncertain");
                continue;
            }
            var question = store.Get<UserQuestion>("question", proposal.QuestionId) ?? throw new InvalidOperationException("Proposal question is missing.");
            if (question.Answer is null)
            { job.Status = "waiting-human"; job.WaitingReason = "Waiting for review of a proposed knowledge change."; return true; }
            if (question.Assumed || !question.Required || question.KnowledgeProposalId != proposal.Id || question.JobId != job.Id || question.SessionId != job.SessionId)
            { Complete(proposal, job, new(false, "Knowledge approval is invalid; no write executed."), "failed"); continue; }
            if (question.Answer != Approve)
            {
                proposal.Status = question.Answer == Reject ? "rejected" : "revision-requested"; proposal.FinishedAt = DateTimeOffset.UtcNow;
                store.Put("knowledge-proposal", proposal.Id, proposal);
                Append(job, $"Knowledge proposal {proposal.Id} was {proposal.Status}. No Cortex write executed. User response: {question.Answer}");
                events.Publish("knowledge.updated", job.Id, new { proposal.Id, proposal.Status, sessionId = job.SessionId }); continue;
            }
            ToolExecutionContext context;
            try
            {
                var session = store.GetSessionMetadata(job.SessionId)!;
                if (session.ProjectId != proposal.ProjectId || projects.Get(proposal.ProjectId).Revision != proposal.ProjectRevision)
                    throw new InvalidOperationException("Project knowledge settings changed after the proposal. Prepare a new proposal.");
                var server = configuration.Get().McpServers.SingleOrDefault(x => x.Enabled && proposal.ToolName.StartsWith("mcp." + x.Id + ".", StringComparison.Ordinal));
                if (server is null || proposal.ProviderFingerprint != CortexOperationPolicy.ProviderFingerprint(server))
                    throw new InvalidOperationException("The Cortex connection changed after review. Prepare a new proposal.");
                if (proposal.Fingerprint != ExecutionReliability.Fingerprint(proposal.ToolName, proposal.Arguments))
                    throw new InvalidOperationException("The reviewed payload no longer matches its saved fingerprint. Prepare a new proposal.");
                CortexOperationPolicy.ValidateExplicitMutation(proposal.ToolName, proposal.Arguments, job.SessionId);
                context = projects.ContextFor(session, profile, job.Id, approvedKnowledgeWrite: true);
                if (context.Cortex?.ProviderFingerprint != proposal.ProviderFingerprint)
                    throw new InvalidOperationException("The Cortex provider changed while preparing the approved request. Prepare a new proposal.");
                if (context.Cortex?.WriteNamespace is not { } writeNamespace || writeNamespace != proposal.Arguments.GetProperty("namespace").GetString() ||
                    !configuration.Get().CortexWriteNamespaces.Contains(writeNamespace, StringComparer.Ordinal))
                    throw new UnauthorizedAccessException("The proposal's namespace is no longer writable.");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
            { Complete(proposal, job, new(false, ex.Message + " No write executed."), "failed"); continue; }
            store.Locked(() =>
            {
                if (store.Get<AgentJob>("job", job.Id)?.CancelRequested != false) throw new OperationCanceledException(token);
                var current = Get(proposal.Id);
                if (current.Status != "pending") throw new InvalidOperationException("Knowledge proposal is no longer pending.");
                proposal.Status = "attempting"; store.Put("knowledge-proposal", proposal.Id, proposal); return true;
            });
            events.Publish("tool.started", job.Id, new { name = proposal.ToolName, arguments = proposal.Arguments, sessionId = job.SessionId });
            try
            {
                var result = CortexOperationPolicy.ConfirmMutation(proposal.ToolName, proposal.Arguments,
                    await tools.ExecuteAsync(new(proposal.ToolName, proposal.Arguments), context, token));
                // A typed no-commit receipt is a failure; a lost or malformed receipt remains uncertain.
                var noCommit = CortexOperationPolicy.HasNoCommitReceipt(result);
                Complete(proposal, job, result.Success || noCommit ? result : result with
                { Content = result.Content + "\nThe write did not return confirmed success. Its effects may be uncertain; inspect Cortex before retrying." },
                    result.Success ? "completed" : noCommit ? "failed" : "uncertain");
            }
            catch (OperationCanceledException)
            {
                Complete(proposal, job, new(false, "Approved knowledge write was cancelled or interrupted; its remote effects are uncertain. Inspect Cortex before retrying."), "uncertain"); throw;
            }
            catch (Exception ex)
            { Complete(proposal, job, new(false, "Approved knowledge write returned no confirmed result: " + ex.Message), "uncertain"); }
        }
        return false;
    }

    private void Complete(KnowledgeProposal proposal, AgentJob job, ToolResult result, string status)
    {
        var bounded = result.Content.Length > 32768 ? result with { Content = result.Content[..32768], Truncated = true } : result;
        store.Locked(() =>
        {
            proposal.Status = status; proposal.Error = bounded.Success ? null : bounded.Content; proposal.FinishedAt = DateTimeOffset.UtcNow;
            store.Put("knowledge-proposal", proposal.Id, proposal);
            var session = store.Append(job.SessionId, "user", $"Knowledge proposal operation {proposal.ToolName}; status={status}; success={bounded.Success}\n{bounded.Content}", proposal.ToolName, origin: "service");
            var evidence = ExecutionReliability.FromTool(proposal.ToolName, proposal.Arguments, bounded, session.Sequence);
            evidence.Id = "knowledge-" + proposal.Id; evidence.SourceSessionId = job.SessionId;
            var saved = store.Get<AgentJob>("job", job.Id)!;
            if (!saved.Evidence.Any(x => x.Id == evidence.Id)) saved.Evidence.Add(evidence);
            job.Evidence = saved.Evidence; store.Put("job", job.Id, saved); return true;
        });
        events.Publish("tool", job.Id, new { name = proposal.ToolName, result = bounded, sessionId = job.SessionId });
        events.Publish("knowledge.updated", job.Id, new { proposal.Id, proposal.Status, sessionId = job.SessionId });
        events.Publish("session.updated", job.Id, new { id = job.SessionId });
    }
    private ToolResult RecordProposalError(AgentJob job, string message) { Append(job, message + " No Cortex write executed."); return new(false, message); }
    private void Append(AgentJob job, string message)
    { store.Append(job.SessionId, "user", message, origin: "service"); events.Publish("session.updated", job.Id, new { id = job.SessionId }); }
}
