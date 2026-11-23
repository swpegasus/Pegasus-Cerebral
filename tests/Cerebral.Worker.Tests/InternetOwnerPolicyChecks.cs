using System.Reflection;
using System.Text.Json;
using Cerebral.Core;
using Cerebral.Contracts.Workflows;
using Cerebral.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

/// <summary>Local fixture records and pure native transitions; no scheduler, provider, browser or installed-state execution.</summary>
public static class InternetOwnerPolicyChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); checks++; }
        var temp = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-internet-fixture-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(temp);
        try
        {
            var store = new StateStore(temp); var config = new Configuration(store); var events = new EventHub();
            var profile = new AgentProfile { Id = "internet-fixture", Name = "Internet fixture", Role = "lead",
                AllowedTools = ["http.fetch"], Capabilities = ["network.http"] };
            store.Put("agent", profile.Id, profile);
            await using var models = new ModelRuntime(store, config.Get, events);
            await using var tools = new ToolDispatcher(config.Get, stateDirectory: temp);
            var effects = new WorkerWorkflowEffects(store, config, models, tools);
            var host = new WorkerWorkflows(store, config, events, [new ConversationWorkflowModule(), effects],
                [ConversationWorkflowModule.PackageBinding, new(effects.Id, WorkerWorkflowEffects.ModuleVersion, true, [])]);
            var policies = new InternetPolicies(store, config, host, events, () => tools.ListManagedInternet());
            using var scheduler = new JobScheduler(store, config, new NeverExecutor(), models, events,
                NullLogger<JobScheduler>.Instance, host);
            var dispatcher = new CommandDispatcher(store, config, models, scheduler, tools, events, workflows: host);
            var administrator = new AgentProfile { Id = "fixture-admin", Name = "Fixture administration agent", Capabilities = ["administration"] };
            store.Put("agent", administrator.Id, administrator);
            foreach (var operation in new[] { "internet.capabilities", "internet.policy.get", "internet.policy.save",
                "conversations.internet.get", "conversations.internet.save", "internet.requests.get", "internet.requests.list",
                "internet.requests.decide", "internet.requests.retire" })
            {
                var deniedRoute = await dispatcher.ExecuteAsync(new(operation, CerebralJson.Element(new { }), administrator.Id));
                Check(!deniedRoute.Success, "administration agent cannot invoke owner route " + operation);
                var described = await dispatcher.ExecuteAsync(new("sdk.describe", CerebralJson.Element(new { id = operation })));
                Check(described.Success, "owner discovers exact Internet schema " + operation);
            }
            async Task<WorkflowRun> Submit(string name) => await host.SubmitAsync(new(name + "-run", name + "-chat", profile.Id,
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "fixture owner request", null, WorkflowContract.EmptyObject), null, default);
            var run = await Submit("one");
            var global = policies.Global();
            Check(global.Policy.Revision == 0 && global.Policy.Mode == "ask" &&
                policies.Main(run.Submission.ConversationId).EffectiveState == "ask", "missing policy defaults to Ask, never Allow");
            global = policies.SaveGlobal(new(global.Policy.Revision, global.CatalogGeneration, "allow", global.Policy.Limits, global.Policy.AllowedTools));
            var main = policies.Main(run.Submission.ConversationId);
            Check(main.EffectiveState == "allowed", "Main inherit follows explicit global Allow");
            InternetMainSaveRequest Change(InternetMainSnapshot current, string choice) => new(current.MainConversationId,
                current.Policy.Revision, current.GlobalPolicy.Revision, current.GlobalPolicyFingerprint, current.RootRevision,
                current.CatalogGeneration, choice, current.Policy.Limits, current.Policy.AllowedTools);
            main = policies.SaveMain(Change(main, "deny"));
            Check(main.EffectiveState == "denied", "chat Deny survives global Allow");
            var beforeDeny = main.Policy.Revision;
            Check(Throws(() => policies.SaveMain(Change(main, "allow") with { ExpectedRevision = beforeDeny - 1 })),
                "stale Main revision cannot grant");
            global = policies.SaveGlobal(new(global.Policy.Revision, global.CatalogGeneration, "off", global.Policy.Limits, global.Policy.AllowedTools));
            main = policies.Main(main.MainConversationId);
            Check(Throws(() => policies.SaveMain(Change(main, "allow"))), "global Off cannot be overridden by owner Main Allow");
            Check(policies.Main(main.MainConversationId).Policy.Revision == beforeDeny, "rejected override preserves policy");
            global = policies.SaveGlobal(new(global.Policy.Revision, global.CatalogGeneration, "ask", global.Policy.Limits, global.Policy.AllowedTools));
            main = policies.SaveMain(Change(policies.Main(main.MainConversationId), "allow"));
            Check(main.EffectiveState == "allowed", "explicit owner Main consent satisfies global Ask within ceiling");
            global = policies.SaveGlobal(new(global.Policy.Revision, global.CatalogGeneration, "ask", global.Policy.Limits, global.Policy.AllowedTools));
            Check(policies.Main(main.MainConversationId).EffectiveState == "ask", "changed global authority invalidates saved consent");
            Check(Throws(() => policies.SaveGlobal(new(global.Policy.Revision - 1, global.CatalogGeneration, "allow",
                global.Policy.Limits, global.Policy.AllowedTools))), "stale global CAS rejected");
            Check(Throws(() => policies.Main("not-a-main")), "unbound conversation cannot select another Main policy");

            async Task<(InternetApprovalRequest Request, InternetOwnerAction Action)> Pending(string name)
            {
                var source = await Submit(name); var scope = policies.Main(source.Submission.ConversationId);
                var tool = scope.EffectiveTools.Single(x => x.ToolId == "http.fetch");
                var authority = new ManagedInternetAuthority(1, scope.WorkerInstallationId, scope.MainConversationId, null,
                    scope.MainConversationId, source.Id, name + "-effect", tool.ToolId, tool.DescriptorFingerprint);
                var action = policies.Preflight(authority, scope.RootRevision, new string('c', 64), "https://example.com")!;
                var claimed = host.Store.TryClaim(source.Id) ?? throw new InvalidOperationException("Fixture source could not be claimed.");
                var state = CerebralJson.Element(new { phase = "questions", pendingInternetRequirement = action });
                host.Store.ApplyStep(source.Id, claimed.Revision, new(state, new AskWorkflowQuestions([
                    new WorkflowQuestion(InternetPolicies.Field(action.ApprovalRequestId), "Review", Choices: ["reviewed"])])));
                return (policies.GetRequest(action.ApprovalRequestId), action);
            }
            InternetDecisionRequest DecideInput(InternetApprovalRequest request, bool allow, string id) => new(request.RequestId,
                request.Revision, request.RequestDigest, request.GlobalRevision, request.GlobalPolicyFingerprint, request.MainRevision,
                request.RootRevision, request.QuestionSetId!, request.QuestionRevision!.Value, allow, id);
            var pending = await Pending("approve");
            Check(pending.Request.ResumeState == "ready" && pending.Request.QuestionRunId == pending.Request.RunId,
                "native owner request becomes ready only after its exact question exists");
            var decision = DecideInput(pending.Request, true, "approve-decision");
            var approved = policies.Decide(decision);
            Check(approved.Status == "approved" && policies.Main(approved.MainConversationId).EffectiveState == "allowed",
                "owner Allow records Main consent and durable decision");
            var policyRevision = policies.Main(approved.MainConversationId).Policy.Revision;
            _ = policies.Decide(decision);
            Check(policies.Main(approved.MainConversationId).Policy.Revision == policyRevision,
                "identical owner retry resumes same answer without granting twice");
            Check(Throws(() => policies.Decide(decision with { Allowed = false })),
                "reused DecisionId cannot change the decision");
            var denied = await Pending("deny");
            var savedBefore = policies.Main(denied.Request.MainConversationId).Policy;
            var denial = policies.Decide(DecideInput(denied.Request, false, "deny-decision"));
            Check(denial.Status == "denied" && policies.Main(denial.MainConversationId).Policy == savedBefore,
                "Deny records outcome and preserves prior policy");
            Check(!policies.Review(denial.RequestId, denial.RequestDigest, denial.RunId, denial.EffectId, denial.ArgumentsSha256),
                "denied decision cannot pass native review");
            var generic = await Pending("generic-answer");
            host.Answer(new("generic-answer-id", generic.Request.RunId, generic.Request.QuestionSetId!,
                generic.Request.QuestionRevision!.Value, new Dictionary<string, string> { [InternetPolicies.Field(generic.Request.RequestId)] = "reviewed" }), null);
            Check(!policies.Review(generic.Request.RequestId, generic.Request.RequestDigest, generic.Request.RunId,
                generic.Request.EffectId, generic.Request.ArgumentsSha256), "generic question answer alone grants nothing");
            Check(Throws(() => host.InternetAuthority(run.Submission, run.Id, "forged-effect", "http.fetch", new string('a', 64))),
                "forged effect/context cannot mint a trusted scope");
            var result = await tools.ExecuteAsync(new("http.fetch", CerebralJson.Element(new { url = "https://example.com/" })),
                new(profile, run.Submission.ConversationId, run.Id, FullAccess: true, EffectId: "no-scope")
                { EnforceProfileScope = true }, default);
            Check(!result.Success && result.EffectiveOutcome == ToolExecutionOutcome.Rejected,
                "a FullAccess context flag without host Internet authority never sends");
            Check(Throws(() => policies.Retire(new(generic.Request.RequestId, generic.Request.Revision, generic.Request.RequestDigest))),
                "active pending requests cannot retire under running work");
            var terminalClaim = host.Store.TryClaim(generic.Request.RunId) ?? throw new InvalidOperationException("Answered fixture was not claimable.");
            host.Store.ApplyStep(terminalClaim.Id, terminalClaim.Revision,
                new(terminalClaim.State, new CompleteWorkflow(WorkflowContract.EmptyObject)));
            var archived = policies.Retire(new(generic.Request.RequestId, generic.Request.Revision, generic.Request.RequestDigest));
            Check(archived.Status == "retired" && policies.GetRequest(archived.RequestId).RequestDigest == generic.Request.RequestDigest &&
                policies.List(archived.MainConversationId, null, 20).Requests.Count == 0,
                "terminal retirement preserves exact evidence while freeing active catalog");
            Check(policies.Retire(new(generic.Request.RequestId, generic.Request.Revision, generic.Request.RequestDigest)).Revision == archived.Revision,
                "exact retirement retry is idempotent");
            checks += ConversationInternetChecks.Run();
            return checks;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var actual = Path.GetFullPath(temp);
            if (!actual.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(actual).StartsWith("cerebral-internet-fixture-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup path escaped its exact temporary root.");
            Directory.Delete(actual, recursive: true);
        }
    }
    private sealed class NeverExecutor : IAgentExecutor
    {
        public Func<SubmitJobRequest, AgentJob>? SubmitHelper { get; set; }
        public Func<bool>? ForegroundPending { get; set; }
        public Task RunAsync(AgentJob job, CancellationToken token) => throw new InvalidOperationException("Unexpected model or legacy job execution.");
    }
    private static bool Throws(Action action)
    { try { action(); return false; } catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException or InternetAccessException) { return true; } }
}

internal static class ConversationInternetChecks
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool yes, string reason) { if (!yes) throw new InvalidOperationException(reason); count++; }
        var harness = typeof(Cerebral.Conversation.ConversationAgentHarness);
        object State(object value) => CerebralJson.Element(value).Deserialize(harness.GetNestedType("HarnessState", BindingFlags.NonPublic)!, CerebralJson.Options)!;
        object Tool(object value) => CerebralJson.Element(value).Deserialize(harness.GetNestedType("ToolData", BindingFlags.NonPublic)!, CerebralJson.Options)!;
        WorkflowStep Call(string name, params object[] args) => (WorkflowStep)harness.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;
        var arguments = CerebralJson.Element(new { name = "http.fetch", arguments = new { url = "https://example.com/" }, workingDirectory = (string?)null });
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(arguments.GetRawText()))).ToLowerInvariant();
        var action = new InternetOwnerAction(1, "internet-owner-approval", new string('d', 64), new string('e', 64),
            "fixture-run", "fixture-effect", "http.fetch", hash, true);
        var state = State(new { phase = "tool", pendingArguments = arguments, pendingName = "http.fetch" });
        var data = Tool(new { name = "http.fetch", content = "", truncated = false, evidence = (object?)null, internetAction = action });
        var effect = new WorkflowEffect(action.EffectId, "cerebral.host:tool", 1, arguments, WorkflowReplayPolicy.Never, WorkflowEffectState.Failed);
        var asking = Call("ReceiveInternetRequirement", state, effect, data, action, "fixture-run");
        Check(asking.Next is AskWorkflowQuestions, "no-send Internet result enters native durable questions");
        var question = (AskWorkflowQuestions)asking.Next;
        var pending = State(asking.State);
        var answered = new WorkflowQuestionSet("fixture-question", 2, question.Fields,
            new Dictionary<string, string> { [question.Fields[0].Id] = "reviewed" });
        var review = Call("ReviewInternetAnswer", pending, answered);
        Check(review.Next is InvokeWorkflowCommand { CommandId: "cerebral.host:internet-review" },
            "answer requests host decision inspection, not tool execution");
        var command = (InvokeWorkflowCommand)review.Next;
        var reviewState = State(review.State);
        WorkflowEffect ReviewEffect(bool allowed) => new("review-effect", command.CommandId, 1, command.Arguments,
            WorkflowReplayPolicy.Never, WorkflowEffectState.Succeeded, CerebralJson.Element(new {
                requestId = action.ApprovalRequestId, requestDigest = action.RequestDigest, originalEffectId = action.EffectId,
                argumentsSha256 = action.ArgumentsSha256, allowed }));
        var deny = Call("ReceiveInternetReview", reviewState, ReviewEffect(false));
        Check(deny.Next is CompleteWorkflow { Success: false }, "owner denial terminates without any replay");
        var allow = Call("ReceiveInternetReview", reviewState, ReviewEffect(true));
        Check(allow.Next is InvokeWorkflowCommand { CommandId: "cerebral.host:tool" } tool &&
            JsonElement.DeepEquals(tool.Arguments, arguments), "confirmed owner decision permits one exact new tool intent");
        var forged = Call("ReceiveInternetRequirement", state, effect, data, action with { NoEffect = false }, "fixture-run");
        Check(forged.Next is CompleteWorkflow { Success: false }, "unproven no-send result cannot enter approval/replay path");
        return count;
    }
}
