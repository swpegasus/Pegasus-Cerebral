using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>
/// Actual Worker effect handler and Engine receipt lookup with temporary
/// SQLite. No generation, command replay, Worker process or provider call.
/// </summary>
internal static class SourceBoundEffectReviewChecks
{
    internal static async Task<int> RunAsync(string exactOwnedFixtureParent)
    {
        if (!Path.IsPathFullyQualified(exactOwnedFixtureParent) ||
            !Directory.Exists(exactOwnedFixtureParent))
            throw new ArgumentException("Provide an existing absolute private fixture parent.");
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(exactOwnedFixtureParent));
        if (parent == Path.GetPathRoot(parent))
            throw new ArgumentException("The fixture parent must be below a volume root.");
        var root = Path.Combine(parent, "source-bound-effect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var complete = false;
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Source-bound effect: " + description);
            checks++;
        }
        try
        {
            var state = new StateStore(Path.Combine(root, "state"));
            var configuration = new Configuration(state);
            WorkerWorkflows? host = null;
            var bridge = new WorkerWorkflowEffects(state, configuration,
                new UnusedModels(), new UnusedTools(), workflowHost: () => host!);
            await using var workflows = new WorkerWorkflows(state, configuration, new EventHub(),
                [bridge], []);
            host = workflows;
            var handler = bridge.Commands.Single(command =>
                command.Descriptor.Id == SourceBoundConditionalRenameContract.CommandId);
            Check(handler.Descriptor.ReplayPolicy == WorkflowReplayPolicy.Never,
                "guarded rename remains a Never-replay effect");

            var source = new SourceBoundWorkflowSourceFacts(1, "fixture-installation",
                "fixture.title", "1.0.0", "fixture-generation", "pegasus.conversation:chat",
                "1.0.1", "source-conversation", "source-profile", "source-request",
                "source-event", 1, new string('a', 64), null, "Original title", 0,
                1, "work-profile", 1);
            var marker = new SourceBoundWorkflowBinding(1, "work-request",
                "work-conversation", source);
            var profile = new AgentProfile { Id = "work-profile", Revision = 1,
                WorkerId = "local", ModelWorkerId = "local" };
            var package = new ServicePluginPackageBinding("fixture.title", "1.0.0",
                true, [], null, "fixture-generation");
            var snapshot = WorkerExecutionSnapshot.Bounded(
                new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version,
                    profile, "", "", null, [], [], [], [], false,
                    "fixture-routing", [package], [])
                { SourceBoundBackground = marker });
            var input = new SourceBoundWorkflowInput(1, source, WorkflowContract.EmptyObject);
            var submission = new WorkflowSubmission("work-request", "work-conversation",
                "work-profile", "fixture.title:generate", "1.0.0", "Title request", null,
                CerebralJson.Element(input), snapshot, Caller: new(EventActorKind.Service,
                    "fixture.title"));
            var rename = new ConversationRenameRequest("rename-request",
                source.SourceDefinitionId, source.SourceConversationId, 0, "New title");
            var args = CerebralJson.Element(new SourceBoundConditionalRenameArguments(rename));
            var context = new WorkflowCommandContext("rename-effect", submission, args)
                { RunId = "work-request" };

            var unknown = await handler.ReconcileAsync(context, default);
            Check(unknown.State == WorkflowEffectState.Unknown &&
                workflows.Store.ReadSourceBoundConversationRenameReceipt(
                    new SourceBoundConversationRenameGuard(rename, "work-request",
                        "work-conversation", source.SourceConversationId,
                        source.SourceRequestId, source.SourceEventId,
                        source.SourceEventSequence, source.SourceInputSha256,
                        source.SourceDefinitionId, source.SourceDefinitionVersion,
                        source.SourceProjectId, source.SourceTitle,
                        source.SourceTitleRevision)) is null,
                "absent exact receipt remains Unknown with no second rename attempt");

            var forged = submission with
            {
                Input = CerebralJson.Element(new SourceBoundWorkflowInput(1,
                    source with { SourceTitle = "Forged title" },
                    WorkflowContract.EmptyObject))
            };
            var rejected = false;
            try
            {
                _ = await handler.ReconcileAsync(
                    new WorkflowCommandContext("rename-effect", forged, args)
                        { RunId = "work-request" }, default);
            }
            catch (UnauthorizedAccessException) { rejected = true; }
            Check(rejected, "public source echo cannot replace the saved host marker");
            complete = true;
            return checks;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (complete)
            {
                var resolved = Path.GetFullPath(root);
                if (!resolved.StartsWith(parent + Path.DirectorySeparatorChar,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing cleanup outside the exact fixture parent.");
                Directory.Delete(resolved, recursive: true);
            }
        }
    }

    private sealed class UnusedModels : IModelRuntimeAdapter
    {
        public ModelAdapterDescriptor Descriptor => new("fixture", "1.0.0", ["text"], false);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Effect fixture cannot use a model.");
    }

    private sealed class UnusedTools : IToolDispatcher
    {
        public bool FullAccessActive { get; set; }
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Effect fixture cannot execute a tool.");
        public Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context,
            ToolExecutionEvidence? previousEvidence, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Effect fixture cannot reconcile a tool.");
    }
}
