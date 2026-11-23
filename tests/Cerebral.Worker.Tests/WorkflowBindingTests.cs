using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.ReviewPlugin;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

public static class WorkflowBindingTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-workflow-bindings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new StateStore(root); var configuration = new Configuration(store); var events = new EventHub();
            store.Put("agent", "reviewer", new AgentProfile { Id = "reviewer", Name = "Review", Capabilities = ["diagnostics"] });
            using var plugin = new ExampleReviewPlugin();
            plugin.Initialize(new ReviewPluginTestHost(Path.Combine(root, "review-data"), AppContext.BaseDirectory));
            ServicePluginPackageBinding[] Packages(string dependencyVersion = "1.2.0", string rootVersion = "1.0.0",
                bool optionalAvailable = false, string unrelatedVersion = "1.0.0", string reason = "Not installed.") =>
            [
                new(plugin.Id, rootVersion, true,
                [new("fixture.required", null, "1.0.0", "2.0.0", false, dependencyVersion, true),
                 new("fixture.optional", "1.0.0", null, null, true, optionalAvailable ? "1.0.0" : null, optionalAvailable, optionalAvailable ? null : reason)]),
                new("fixture.required", dependencyVersion, true, []),
                new("fixture.optional", "1.0.0", optionalAvailable, []),
                new("fixture.unrelated", unrelatedVersion, true, [])
            ];
            WorkerWorkflows Host(ServicePluginPackageBinding[] bindings) => new(store, configuration, events, [plugin], bindings);
            async Task<(WorkerWorkflows Host, SubmitWorkflowRequest Request, WorkflowRun Run)> Ready(string id)
            {
                var host = Host(Packages());
                var request = new SubmitWorkflowRequest(id, id + "-chat", "reviewer", ExampleReviewPlugin.DefinitionId,
                    "1.0.0", "Measure this passage.", null, CerebralJson.Element(new { text = "one two three" }));
                var run = host.Submit(request, null);
                run = await host.Runtime.RunAsync(run.Id);
                run = host.Answer(new(id + "-answer", run.Id, run.Questions!.Id, run.Questions.Revision,
                    new Dictionary<string, string> { ["label"] = "Passage", ["style"] = "concise" }), null);
                return (host, request, run);
            }

            var changed = await Ready("changed-dependency");
            var next = Host(Packages(dependencyVersion: "1.3.0"));
            Check(next.Submit(changed.Request, null).Submission.SettingsSnapshot.GetRawText() == changed.Run.Submission.SettingsSnapshot.GetRawText(),
                "submission retry preserves the original binding even after a compatible dependency upgrade");
            var held = await next.Runtime.RunAsync(changed.Run.Id);
            Check(held.Status == WorkflowStatus.Waiting && held.WaitingReason!.Contains("dependencies changed", StringComparison.Ordinal) && held.Effect is null,
                "compatible installed upgrade cannot change an accepted workflow's package graph");
            var restored = Host(Packages());
            await restored.Runtime.ResumeAsync(held.Id, held.Revision);
            var completed = await restored.Runtime.RunAsync(held.Id);
            Check(completed.Status == WorkflowStatus.Completed && completed.Result?.GetProperty("wordCount").GetInt32() == 3,
                "restoring the accepted package versions permits normal continuation");

            var newOptional = await Ready("new-optional");
            Check((await Host(Packages(optionalAvailable: true)).Runtime.RunAsync(newOptional.Run.Id)).Status == WorkflowStatus.Waiting,
                "newly available optional feature does not silently alter accepted behavior");
            newOptional.Host.Store.Cancel(newOptional.Run.Id);
            var changedRoot = await Ready("changed-root");
            Check((await Host(Packages(rootVersion: "1.1.0")).Runtime.RunAsync(changedRoot.Run.Id)).Status == WorkflowStatus.Waiting,
                "package revision is checked independently of the workflow definition version");
            changedRoot.Host.Store.Cancel(changedRoot.Run.Id);
            var unrelated = await Ready("unrelated");
            Check((await Host(Packages(unrelatedVersion: "2.0.0", reason: "Source is offline.")).Runtime.RunAsync(unrelated.Run.Id)).Status == WorkflowStatus.Completed,
                "unrelated package changes and optional diagnostic wording do not block accepted work");

            // Simulate a saved prepared effect crossing into a package that was never accepted.
            var unbound = await Ready("unbound");
            var claimed = unbound.Host.Store.TryClaim(unbound.Run.Id)!;
            var foreign = new ForeignCommand();
            unbound.Host.Store.ApplyStep(claimed.Id, claimed.Revision,
                new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand(foreign.Descriptor.Id, 1, WorkflowContract.EmptyObject)), foreign.Descriptor);
            var extraHost = new WorkerWorkflows(store, configuration, events, [plugin, new ForeignModule(foreign)],
                Packages().Append(new("fixture.foreign", "1.0.0", true, [])).ToArray());
            extraHost.Store.RecoverInterrupted();
            var blocked = await extraHost.Runtime.RunAsync(claimed.Id);
            Check(blocked.Status == WorkflowStatus.Waiting && foreign.Calls == 0 && blocked.Effect?.State == WorkflowEffectState.Prepared,
                "a prepared effect cannot use an undeclared or unaccepted plugin");
            extraHost.Store.Cancel(blocked.Id);
            Check(extraHost.Journal.ReadPage(claimed.Submission.ConversationId).Events.All(x => x.Type != CerebralEventTypes.ToolResult),
                "blocked package execution does not fabricate an effect result");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        Console.WriteLine("Workflow package bindings: 8 SQLite/fixture checks passed; no LLM or package download.");
    }

    private static void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
    private sealed class ForeignModule(ForeignCommand command) : IWorkflowModule
    {
        public string Id => "fixture.foreign";
        public IReadOnlyList<IWorkflowDefinition> Workflows => [];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [command];
    }
    private sealed class ForeignCommand : IWorkflowCommandHandler
    {
        public int Calls;
        public WorkflowCommandDescriptor Descriptor { get; } = new("fixture.foreign:command", 1, "Unaccepted fixture command",
            WorkflowContract.EmptyObject, WorkflowContract.EmptyObject, ["diagnostics"], WorkflowReplayPolicy.Idempotent);
        public Task<WorkflowCommandResult> ExecuteAsync(WorkflowCommandContext context, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new WorkflowCommandResult(WorkflowEffectState.Succeeded, WorkflowContract.EmptyObject)); }
    }
}
