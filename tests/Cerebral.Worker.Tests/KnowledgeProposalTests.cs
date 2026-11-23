using System.Text.Json;
using Cerebral.Core;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static class KnowledgeProposalTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-knowledge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var assertions = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); assertions++; Console.WriteLine("PASS: " + message); }
        try
        {
            var store = new StateStore(Path.Combine(root, "data")); var config = new Configuration(store); var events = new EventHub();
            var host = config.Get(); host.ModelDirectory = Path.Combine(root, "models");
            host.McpServers = [new() { Id = "cortex", Executable = Environment.ProcessPath!, Enabled = true }];
            host.CortexNamespaces = ["project.fixture"]; host.CortexWriteNamespaces = ["project.fixture"]; config.Save(host);
            store.Put("navigation", "sidebar", new NavigationState { Nodes = [new() { Id = "project", Kind = "project", Title = "Fixture", Path = root }] });
            var projects = new ProjectKnowledge(store, config, events);
            var settings = projects.Save(new() { ProjectId = "project", CortexServerId = "cortex", ReadNamespaces = ["project.fixture"], WriteNamespace = "project.fixture" });
            var profile = new AgentProfile { Id = "lead", Name = "Fixture", ModelId = "fixture", AllowedRoots = [root],
                AllowedTools = ["mcp.cortex.cortex_knowledge"], Capabilities = ["mcp.cortex", "cortex.write"] };
            store.Put("agent", profile.Id, profile);
            var executor = new FakeTools(); var proposals = new KnowledgeProposals(store, config, executor, events);
            AgentJob Job()
            {
                var session = new TaskSession { ProfileId = profile.Id, ProjectId = "project" }; store.Put("session", session.Id, session);
                var job = new AgentJob { SessionId = session.Id, ProfileId = profile.Id, Profile = profile, Status = "running" }; store.Put("job", job.Id, job); return job;
            }
            var arguments = CerebralJson.Element(new { action = "revise", @namespace = "project.fixture", recordId = "character", title = "Character", content = "Mira has blue eyes.", expectedRevision = 2, idempotencyKey = "fixture-revise", provenance = new { source = "user" } });
            KnowledgeProposal Stage(AgentJob job)
            {
                var result = proposals.ProposeIfRequired(job, profile, "mcp.cortex.cortex_knowledge", arguments);
                Check(result is { Success: true } && job.Status == "waiting-human", "durable write becomes a required review before execution");
                return proposals.List(job.SessionId).Single();
            }
            void Answer(KnowledgeProposal proposal, string text)
            { var question = store.Get<UserQuestion>("question", proposal.QuestionId)!; question.Answer = text; store.Put("question", question.Id, question); }

            foreach (var invalid in new[] {
                CerebralJson.Element(new { action = "upsert", @namespace = "project.fixture" }),
                CerebralJson.Element(new { action = "revise", @namespace = "project.fixture", recordId = "character", content = "change", expectedRevision = 2, provenance = new { source = "user" } }),
                CerebralJson.Element(new { action = "revise", recordId = "character", content = "change", expectedRevision = 2, idempotencyKey = "missing-namespace", provenance = new { source = "user" } }),
                CerebralJson.Element(new { action = "revise", @namespace = "project.fixture", recordId = "character", content = "change", idempotencyKey = "missing-revision", provenance = new { source = "user" } }),
                CerebralJson.Element(new { action = "revise", @namespace = "project.fixture", recordId = "character", content = "change", expectedRevision = 2, idempotencyKey = "missing-provenance" })
            })
            {
                var invalidJob = Job();
                Check(proposals.ProposeIfRequired(invalidJob, profile, "mcp.cortex.cortex_knowledge", invalid) is { Success: false } &&
                    proposals.List(invalidJob.SessionId).Count == 0 && !store.List<UserQuestion>("question").Any(x => x.JobId == invalidJob.Id) && executor.Calls == 0,
                    "incomplete or unsupported knowledge write is rejected before asking for approval");
            }

            var firstJob = Job(); var first = Stage(firstJob);
            var question = store.Get<UserQuestion>("question", first.QuestionId)!;
            Check(question.Required && !question.CanAssume && question.KnowledgeProposalId == first.Id && executor.Calls == 0, "proposal is bound to an exact required question and has no provider effects");
            Check(await proposals.ProcessPendingAsync(firstJob, profile, CancellationToken.None) && executor.Calls == 0, "unanswered proposal keeps waiting without executing");
            Answer(first, KnowledgeProposals.Approve);
            Check(!await proposals.ProcessPendingAsync(firstJob, profile, CancellationToken.None) && executor.Calls == 1, "exact user approval executes the saved payload once");
            Check(JsonElement.DeepEquals(executor.LastArguments, arguments) && executor.LastContext!.Cortex!.KnowledgeWriteApproved, "approved dispatch uses original arguments and explicit project approval scope");
            await proposals.ProcessPendingAsync(firstJob, profile, CancellationToken.None);
            Check(executor.Calls == 1 && proposals.Get(first.Id).Status == "completed", "completed approved write is not replayed");

            var rejectedJob = Job(); var rejected = Stage(rejectedJob); Answer(rejected, KnowledgeProposals.Reject);
            await proposals.ProcessPendingAsync(rejectedJob, profile, CancellationToken.None);
            Check(proposals.Get(rejected.Id).Status == "rejected" && executor.Calls == 1, "reject leaves Cortex unchanged");

            var reviseJob = Job(); var revise = Stage(reviseJob); Answer(revise, "Use green eyes instead.");
            await proposals.ProcessPendingAsync(reviseJob, profile, CancellationToken.None);
            Check(proposals.Get(revise.Id).Status == "revision-requested" && executor.Calls == 1, "custom answer requests a revision rather than being interpreted as approval");

            var staleJob = Job(); var stale = Stage(staleJob); Answer(stale, KnowledgeProposals.Approve);
            settings = projects.Save(settings);
            await proposals.ProcessPendingAsync(staleJob, profile, CancellationToken.None);
            Check(proposals.Get(stale.Id).Status == "failed" && executor.Calls == 1, "project settings revision change invalidates approval before a write");

            var cancelledJob = Job(); var cancelled = Stage(cancelledJob); Answer(cancelled, KnowledgeProposals.Approve);
            var saved = store.Get<AgentJob>("job", cancelledJob.Id)!; saved.CancelRequested = true; store.Put("job", saved.Id, saved);
            var cancellationObserved = false;
            try { await proposals.ProcessPendingAsync(cancelledJob, profile, CancellationToken.None); } catch (OperationCanceledException) { cancellationObserved = true; }
            Check(cancellationObserved && executor.Calls == 1, "Stop before approved dispatch prevents the write");

            var uncertainJob = Job(); var uncertain = Stage(uncertainJob); Answer(uncertain, KnowledgeProposals.Approve);
            executor.ThrowCancellation = true;
            try { await proposals.ProcessPendingAsync(uncertainJob, profile, CancellationToken.None); } catch (OperationCanceledException) { }
            executor.ThrowCancellation = false;
            await proposals.ProcessPendingAsync(uncertainJob, profile, CancellationToken.None);
            Check(proposals.Get(uncertain.Id).Status == "uncertain" && executor.Calls == 2, "interrupted approved write stays uncertain and is not automatically retried");
            Check(store.Get<AgentJob>("job", uncertainJob.Id)!.Evidence.Any(x => !x.Success), "unconfirmed knowledge result reaches service-owned failure evidence");

            var restartJob = Job(); var restart = Stage(restartJob);
            restart.Status = "attempting"; store.Put("knowledge-proposal", restart.Id, restart);
            proposals.RecoverInterrupted();
            Check(proposals.Get(restart.Id).Status == "uncertain" && executor.Calls == 2,
                "service restart exposes interrupted attempts without replaying them");

            var assumedJob = Job(); var assumed = Stage(assumedJob); Answer(assumed, KnowledgeProposals.Approve);
            var assumedQuestion = store.Get<UserQuestion>("question", assumed.QuestionId)!; assumedQuestion.Assumed = true; store.Put("question", assumedQuestion.Id, assumedQuestion);
            await proposals.ProcessPendingAsync(assumedJob, profile, CancellationToken.None);
            Check(proposals.Get(assumed.Id).Status == "failed" && executor.Calls == 2, "assumed answers cannot authorize knowledge writes");

            Check(proposals.ProposeIfRequired(Job(), profile, "mcp.cortex.cortex_search", CerebralJson.Element(new { text = "Mira" })) is null, "ordinary knowledge retrieval does not ask for approval");
            var endpointJob = Job(); var endpointProposal = Stage(endpointJob); Answer(endpointProposal, KnowledgeProposals.Approve);
            var changedHost = config.Get(); changedHost.McpServers.Single().Arguments = ["--different-provider"]; config.Save(changedHost);
            var before = executor.Calls;
            await proposals.ProcessPendingAsync(endpointJob, profile, CancellationToken.None);
            Check(proposals.Get(endpointProposal.Id).Status == "failed" && executor.Calls == before, "changed provider target invalidates existing approval before dispatch");
            changedHost.McpServers.Single().Arguments = []; config.Save(changedHost);

            foreach (var disposition in new[] { "conflict", "stale-revision", "not-found", "rejected" })
            {
                var conflictJob = Job(); var conflict = Stage(conflictJob); Answer(conflict, KnowledgeProposals.Approve);
                executor.Disposition = disposition;
                await proposals.ProcessPendingAsync(conflictJob, profile, CancellationToken.None);
                Check(proposals.Get(conflict.Id).Status == "failed" && store.Get<AgentJob>("job", conflictJob.Id)!.Evidence.Any(x => !x.Success),
                    "normal MCP response with " + disposition + " is a failed write");
            }
            executor.Disposition = "idempotent";
            var retryJob = Job(); var retry = Stage(retryJob); Answer(retry, KnowledgeProposals.Approve);
            await proposals.ProcessPendingAsync(retryJob, profile, CancellationToken.None);
            Check(proposals.Get(retry.Id).Status == "completed", "matching idempotent receipt confirms the same reviewed write");
            executor.Disposition = "appended";
            foreach (var defect in new[] { "namespace", "recordId", "revision", "missing", "truncated" })
            {
                executor.ReceiptDefect = defect;
                var receiptJob = Job(); var receipt = Stage(receiptJob); Answer(receipt, KnowledgeProposals.Approve);
                await proposals.ProcessPendingAsync(receiptJob, profile, CancellationToken.None);
                Check(proposals.Get(receipt.Id).Status == "uncertain", "mismatched " + defect + " receipt cannot confirm a write");
            }
            executor.ReceiptDefect = null;
            settings.RequireKnowledgeApproval = false; settings = projects.Save(settings);
            profile.PluginSettings["fixture.module"] = CerebralJson.Element(new { enabled = true }); store.Put("agent", profile.Id, profile);
            var callsBeforeBook = executor.Calls;
            var bookJob = Job();
            Check(proposals.ProposeIfRequired(bookJob, profile, "mcp.cortex.cortex_knowledge", arguments) is null &&
                  proposals.List(bookJob.SessionId).Count == 0 && executor.Calls == callsBeforeBook,
                "Module settings do not turn Cortex into module storage or add a separate knowledge-review policy");
            Console.WriteLine($"Knowledge proposals: {assertions} deterministic approval and SQLite checks passed; no live Cortex writes.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup path.");
            Directory.Delete(full, true);
        }
    }
    private sealed class FakeTools : IToolDispatcher
    {
        public string Disposition = "appended"; public string? ReceiptDefect;
        public int Calls; public bool ThrowCancellation; public JsonElement LastArguments; public ToolExecutionContext? LastContext;
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            Calls++; LastArguments = call.Arguments.Clone(); LastContext = context;
            if (ThrowCancellation) throw new OperationCanceledException(cancellationToken);
            var receipt = new
            {
                effectiveNamespaces = new[] { ReceiptDefect == "namespace" ? "wrong.namespace" : call.Arguments.GetProperty("namespace").GetString()! },
                disposition = Disposition,
                record = new { @namespace = call.Arguments.GetProperty("namespace").GetString(), recordId = ReceiptDefect == "recordId" ? "wrong-record" : call.Arguments.GetProperty("recordId").GetString(),
                    revision = ReceiptDefect == "revision" ? 99 : call.Arguments.GetProperty("expectedRevision").GetInt64() + 1 }
            };
            var result = ReceiptDefect == "missing" ? """{"isError":false}""" : JsonSerializer.Serialize(new { isError = false, structuredContent = receipt });
            return Task.FromResult(new ToolResult(true, result, ReceiptDefect == "truncated"));
        }
    }
}
