using System.Text.Json;
using Cerebral.Core;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerebral.Worker.Tests;

internal static class ActionLinearizationTests
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cerebral-action-linearization-" + Guid.NewGuid().ToString("N")));
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var source = Path.Combine(project, "result.txt"); await File.WriteAllTextAsync(source, "retained interface fixture");
        var store = new StateStore(Path.Combine(root, "data")); var configuration = new Configuration(store); var events = new EventHub();
        await using var runtime = new ModelRuntime(store, configuration.Get, events);
        var tools = new NoTools(); var engine = new AgentEngine(store, configuration, runtime, tools, events);
        using var scheduler = new JobScheduler(store, configuration, engine, runtime, events, NullLogger<JobScheduler>.Instance);
        var assertions = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            assertions++; Console.WriteLine("PASS: " + name);
        }

        var host = configuration.Get();
        host.McpServers = [new() { Id = "cortex", Executable = Environment.ProcessPath!, Enabled = true }];
        host.CortexNamespaces = ["project.fixture"]; host.CortexWriteNamespaces = ["project.fixture"];
        configuration.Save(host);
        store.Put("navigation", "sidebar", new NavigationState
        {
            Nodes = [new() { Id = "project", Kind = "project", Title = "Fixture", Path = project }]
        });
        var projects = new ProjectKnowledge(store, configuration, events);
        projects.Save(new()
        {
            ProjectId = "project", CortexServerId = "cortex", ReadNamespaces = ["project.fixture"],
            WriteNamespace = "project.fixture"
        });
        var profile = new AgentProfile
        {
            Id = "lead", Name = "Lead", Role = "lead", ModelId = "fixture", AllowedRoots = [project],
            AllowedTools = ["file.read", "mcp.cortex.cortex_knowledge"],
            Capabilities = ["agents.delegate", "filesystem.read", "mcp.cortex", "cortex.write"]
        };
        store.Put("agent", profile.Id, profile);
        engine.SubmitHelper = request =>
        {
            var child = new AgentJob
            {
                Id = Guid.NewGuid().ToString("N"), SessionId = "helper-" + Guid.NewGuid().ToString("N"),
                ProfileId = request.ProfileId, ParentJobId = request.ParentJobId, Prompt = request.Prompt,
                Status = "queued", Profile = profile
            };
            store.Put("job", child.Id, child); return child;
        };

        try
        {
            var longFormObjective = new LongFormObjective(600, "600 words", null)
            {
                RequiresNewArtifact = true, SourceRequestSequence = 1
            };
            var longFormAction = CerebralJson.Element(new
            {
                action = "longform.plan", path = Path.Combine(project, "draft.md"),
                sections = new[]
                {
                    new { title = "Opening", purpose = "Establish the setting." },
                    new { title = "Middle", purpose = "Develop the conflict." },
                    new { title = "Ending", purpose = "Resolve the conflict." }
                }
            });
            var planStale = Fixture("plan-stale");
            var stalePlan = await SteerBeforeAsync(planStale, "Steering wins before plan adoption.",
                () => engine.TryAdoptLongFormPlan(planStale.Job, planStale.Session.Revision, longFormObjective, longFormAction));
            Check(!stalePlan.RevisionMatched && store.Get<LongFormState>("longform", planStale.Session.Id) is null,
                "steering committed first invalidates the long-form plan before its ledger is retained");

            var planFirst = Fixture("plan-first");
            var adoptedPlan = engine.TryAdoptLongFormPlan(planFirst.Job, planFirst.Session.Revision, longFormObjective, longFormAction);
            scheduler.Steer(planFirst.Job.Id, "Steering follows the official long-form plan.", "plan-after");
            var planMessages = store.Get<TaskSession>("session", planFirst.Session.Id)!.Messages;
            Check(adoptedPlan.RevisionMatched && store.Get<LongFormState>("longform", planFirst.Session.Id) is { SourceJobId: "plan-first-job" } &&
                  planMessages.FindIndex(x => x.Content.StartsWith("The service adopted a measured long-form plan", StringComparison.Ordinal)) <
                  planMessages.FindIndex(x => x.Content == "Steering follows the official long-form plan."),
                "long-form plan committed first is retained and ordered before later steering");

            var planCancelled = Fixture("plan-cancelled");
            using var planCancelEntered = new ManualResetEventSlim(); using var allowPlanCancel = new ManualResetEventSlim();
            var planCancellation = Task.Run(() => store.Locked(() =>
            {
                planCancelEntered.Set(); allowPlanCancel.Wait(); return scheduler.Cancel(planCancelled.Job.Id);
            }));
            Check(planCancelEntered.Wait(TimeSpan.FromSeconds(5)), "the deterministic plan cancellation race acquired the durable state boundary first");
            var cancelledPlanAdoption = Task.Run(() =>
            {
                try
                {
                    engine.TryAdoptLongFormPlan(planCancelled.Job, planCancelled.Session.Revision, longFormObjective, longFormAction);
                    return false;
                }
                catch (OperationCanceledException) { return true; }
            });
            allowPlanCancel.Set(); await planCancellation;
            Check(await cancelledPlanAdoption && store.Get<LongFormState>("longform", planCancelled.Session.Id) is null &&
                  !store.Get<TaskSession>("session", planCancelled.Session.Id)!.Messages.Any(x => x.Role == "assistant") &&
                  store.Get<AgentJob>("job", planCancelled.Job.Id) is { Status: "cancelled", CancelRequested: true },
                "cancellation committed first prevents a long-form plan from being adopted after the assignment stops");

            var delegateAction = CerebralJson.Element(new { action = "delegate", profileId = "helper", prompt = "Inspect the bounded fixture." });
            var delegateStale = Fixture("delegate-stale");
            var staleDelegation = await SteerBeforeAsync(delegateStale, "Steering wins before delegate adoption.",
                () => engine.TryAdoptDelegation(delegateStale.Job, profile, ["helper"], delegateAction, delegateStale.Session.Revision));
            Check(!staleDelegation.RevisionMatched && !store.List<AgentJob>("job").Any(x => x.ParentJobId == delegateStale.Job.Id),
                "steering committed first invalidates delegation before any helper is admitted");

            var delegateFirst = Fixture("delegate-first");
            var adoptedDelegation = engine.TryAdoptDelegation(delegateFirst.Job, profile, ["helper"], delegateAction, delegateFirst.Session.Revision);
            scheduler.Steer(delegateFirst.Job.Id, "Steering follows the official delegation.", "delegate-after");
            var delegateMessages = store.Get<TaskSession>("session", delegateFirst.Session.Id)!.Messages;
            Check(adoptedDelegation is { RevisionMatched: true, Value.AcceptedJobIds.Count: 1 } &&
                  store.Get<AgentJob>("job", delegateFirst.Job.Id)!.Status == "waiting-helper" &&
                  delegateMessages.FindIndex(x => x.Content.StartsWith("Delegated assignment", StringComparison.Ordinal)) <
                  delegateMessages.FindIndex(x => x.Content == "Steering follows the official delegation."),
                "delegation committed first is durable and ordered before later steering");

            var presentAction = CerebralJson.Element(new { action = "present", kind = "file", path = source });
            var presentStale = Fixture("present-stale");
            var stalePresentation = await SteerBeforeAsync(presentStale, "Steering wins before presentation adoption.",
                () => engine.TryAdoptPresentation(presentStale.Job, profile, ["file"], presentAction, presentStale.Session.Revision));
            Check(!stalePresentation.RevisionMatched && !store.List<InterfaceRequest>("interface").Any(x => x.JobId == presentStale.Job.Id),
                "steering committed first invalidates presentation before an interface request is retained");

            var presentFirst = Fixture("present-first");
            var adoptedPresentation = engine.TryAdoptPresentation(presentFirst.Job, profile, ["file"], presentAction, presentFirst.Session.Revision);
            scheduler.Steer(presentFirst.Job.Id, "Steering follows the official presentation request.", "present-after");
            var presentation = store.List<InterfaceRequest>("interface").Single(x => x.JobId == presentFirst.Job.Id);
            var presentMessages = store.Get<TaskSession>("session", presentFirst.Session.Id)!.Messages;
            Check(adoptedPresentation.RevisionMatched && presentation.Status == "pending" &&
                  presentMessages.FindIndex(x => x.Content.StartsWith("Requesting interface presentation", StringComparison.Ordinal)) <
                  presentMessages.FindIndex(x => x.Content == "Steering follows the official presentation request."),
                "presentation committed first is retained and ordered before later steering without launching it");

            var knowledgeArguments = CerebralJson.Element(new
            {
                action = "revise", @namespace = "project.fixture", recordId = "character", title = "Character",
                content = "Mira has blue eyes.", expectedRevision = 2, idempotencyKey = "linearized-revise",
                provenance = new { source = "user" }
            });
            var knowledgeStale = Fixture("knowledge-stale", projectId: "project");
            var staleKnowledge = await SteerBeforeAsync(knowledgeStale, "Steering wins before knowledge proposal adoption.",
                () => engine.TryAdoptKnowledgeProposal(knowledgeStale.Job, profile, "mcp.cortex.cortex_knowledge",
                    knowledgeArguments, knowledgeStale.Session.Revision));
            Check(!staleKnowledge.RevisionMatched && !store.List<KnowledgeProposal>("knowledge-proposal").Any(x => x.JobId == knowledgeStale.Job.Id),
                "steering committed first invalidates the knowledge proposal before an approval card is retained");

            var knowledgeFirst = Fixture("knowledge-first", projectId: "project");
            var adoptedKnowledge = engine.TryAdoptKnowledgeProposal(knowledgeFirst.Job, profile, "mcp.cortex.cortex_knowledge",
                knowledgeArguments, knowledgeFirst.Session.Revision);
            scheduler.Steer(knowledgeFirst.Job.Id, "Steering follows the official knowledge proposal.", "knowledge-after");
            var proposal = store.List<KnowledgeProposal>("knowledge-proposal").Single(x => x.JobId == knowledgeFirst.Job.Id);
            var knowledgeMessages = store.Get<TaskSession>("session", knowledgeFirst.Session.Id)!.Messages;
            Check(adoptedKnowledge is { RevisionMatched: true, Value: not null } && proposal.Status == "pending" &&
                  store.Get<AgentJob>("job", knowledgeFirst.Job.Id)!.Status == "waiting-human" &&
                  knowledgeMessages.FindIndex(x => x.Content.Contains("Knowledge proposal", StringComparison.Ordinal)) <
                  knowledgeMessages.FindIndex(x => x.Content == "Steering follows the official knowledge proposal."),
                "knowledge proposal committed first is retained and ordered before later steering without a Cortex write");

            var finalAction = CerebralJson.Element(new { action = "final", outcome = "completed", text = "Finished response.", resolutions = Array.Empty<object>() });
            var finalStale = Fixture("final-stale");
            var staleFinal = await SteerBeforeAsync(finalStale, "Steering wins before final adoption.",
                () => engine.TryAdoptFinalAction(finalStale.Job, finalStale.Session.Revision, finalAction, null, [], false));
            Check(!staleFinal.RevisionMatched && store.Get<AgentJob>("job", finalStale.Job.Id)!.Status == "running" &&
                  !store.Get<TaskSession>("session", finalStale.Session.Id)!.Messages.Any(x => x.Role == "assistant"),
                "steering committed first invalidates the stale final response");

            var finalFirst = Fixture("final-first");
            var adoptedFinal = engine.TryAdoptFinalAction(finalFirst.Job, finalFirst.Session.Revision, finalAction, null, [], false);
            var steerRejected = false;
            try { scheduler.Steer(finalFirst.Job.Id, "This must not be silently accepted.", "final-after"); }
            catch (InvalidOperationException) { steerRejected = true; }
            var finalSaved = store.Get<AgentJob>("job", finalFirst.Job.Id)!;
            var finalMessages = store.Get<TaskSession>("session", finalFirst.Session.Id)!.Messages;
            Check(adoptedFinal is { RevisionMatched: true, Value.Accepted: true } && finalSaved.Status == "completed" &&
                  finalSaved.Outcome == "completed" && finalSaved.Result == "Finished response." && finalSaved.FinishedAt is not null,
                "final adoption durably records the terminal job outcome before releasing the gate");
            Check(steerRejected, "steering is rejected after the final action makes the retained job terminal");
            Check(finalMessages.Count(x => x.Content == "Finished response.") == 1 &&
                  !finalMessages.Any(x => x.Content == "This must not be silently accepted."),
                "the adopted final response is retained once without a silently lost later steering message");

            Console.WriteLine($"Model action linearization: {assertions} deterministic SQLite race checks passed; no model, tool, Cortex write, or application execution.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temporaryRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the exact temporary test workspace.");
            Directory.Delete(root, recursive: true);
        }

        (TaskSession Session, AgentJob Job) Fixture(string id, string? projectId = null)
        {
            var session = new TaskSession
            {
                Id = id + "-session", ProfileId = profile.Id, ProjectId = projectId, Revision = 1,
                Messages = [new() { Sequence = 1, Role = "user", Origin = "user", Content = "Initial request for " + id + "." }]
            };
            var job = new AgentJob
            {
                Id = id + "-job", SessionId = session.Id, ProfileId = profile.Id, Prompt = session.Messages[0].Content,
                StartSequence = 1, Status = "running", Profile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(profile))
            };
            store.Put("session", session.Id, session); store.Put("job", job.Id, job); return (session, job);
        }

        async Task<T> SteerBeforeAsync<T>((TaskSession Session, AgentJob Job) fixture, string text, Func<T> action)
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var steering = Task.Run(() => store.LinearizedSession(fixture.Session.Id, () =>
            {
                entered.Set(); release.Wait(); return scheduler.Steer(fixture.Job.Id, text, fixture.Job.Id + "-steer");
            }));
            if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Steering did not acquire the session gate.");
            var attempted = Task.Run(action); release.Set(); await steering; return await attempted;
        }
    }

    private sealed class NoTools : IToolDispatcher
    {
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Action linearization tests do not execute tools.");
    }
}
