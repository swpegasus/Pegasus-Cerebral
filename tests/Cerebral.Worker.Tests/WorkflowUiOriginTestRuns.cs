using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Workflows;
using Cerebral.Tools;
using Cerebral.Worker;
using System.Text.Json;

namespace Cerebral.Worker.Tests;

// Shared isolated metadata fixture. All authority comes from real durable admissions; this
// does not call a provider, execute a semantic tool, or bypass the production origin resolver.
internal static class WorkflowUiOriginTestRuns
{
    internal static IReadOnlyList<ToolDescriptor> Catalog(StateStore store, AgentProfile profile,
        IReadOnlyList<ToolDescriptor>? extensions = null)
    {
        // Discover real built-in schemas against an explicitly offline settings value. The
        // dispatcher filters by the fixture's existing grants; this never adds profile authority.
        var dispatcher = new ToolDispatcher(() => new HostSettings(), stateDirectory: store.DirectoryPath);
        try
        {
            var builtins = dispatcher.ListAsync(profile, CancellationToken.None).GetAwaiter().GetResult();
            extensions ??= new InterfaceToolExtension(store, new EventHub(), () => 2).Tools
                .Concat(new PluginControlToolExtension(store, new EventHub()).Tools).ToArray();
            var suppliedNames = extensions.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
            // Existing catalog fakes may intentionally bind a selected descriptor exactly.
            // Add only absent built-ins, preserving those schemas and malformed-catalog negatives.
            return extensions.Concat(builtins.Where(tool => !suppliedNames.Contains(tool.Name))).Where(tool =>
                WorkerExecutionSnapshot.AllowsTool(profile, tool.Name) &&
                profile.Capabilities.Contains(tool.Capability, StringComparer.Ordinal)).ToArray();
        }
        finally { dispatcher.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    internal static string Routing(StateStore store) => WorkerExecutionSnapshot.RoutingFingerprint(
        store.Get<HostSettings>("settings", "host") ?? new HostSettings());

    internal static ToolExecutionContext Metadata(StateStore store, ToolExecutionContext context,
        IReadOnlyList<ToolDescriptor>? tools = null)
    {
        var workflows = new WorkflowStore(new EventJournal(Path.Combine(store.DirectoryPath, "engine.db")));
        if (workflows.TryGet(context.JobId) is not null) return context;
        FinishPrevious(workflows, context.SessionId);
        var profile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(context.Profile));
        tools = Catalog(store, profile, tools);
        var snapshot = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version, profile, "", "", null,
            tools, profile.Capabilities.ToArray(), profile.AllowedTools.ToArray(), profile.AllowedRoots.ToArray(),
            context.FullAccess, Routing(store), [], []);
        var run = workflows.Submit(new WorkflowSubmission(context.JobId, context.SessionId, profile.Id,
            ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
            "Isolated owner UI metadata fixture.", null, WorkflowContract.EmptyObject, CerebralJson.Element(snapshot),
            Caller: new(EventActorKind.User, "fixture-owner")));
        run = workflows.TryClaim(run.Id)!;
        workflows.ApplyStep(run.Id, run.Revision,
            new(WorkflowContract.EmptyObject, new CompleteWorkflow(WorkflowContract.EmptyObject)));
        return context;
    }

    internal static ToolExecutionContext Live(StateStore store, ToolExecutionContext context, ToolCall call)
    {
        var workflows = new WorkflowStore(new EventJournal(Path.Combine(store.DirectoryPath, "engine.db")));
        var run = workflows.TryGet(context.JobId);
        var empty = WorkflowContract.EmptyObject;
        if (run is null)
        {
            FinishPrevious(workflows, context.SessionId);
            var profile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(context.Profile));
            var tools = Catalog(store, profile);
            var snapshot = new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version, profile, "", "", null,
                tools, profile.Capabilities.ToArray(), profile.AllowedTools.ToArray(), profile.AllowedRoots.ToArray(),
                context.FullAccess, Routing(store), [], []);
            run = workflows.Submit(new(context.JobId, context.SessionId, profile.Id,
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                "Actual dispatched UI fixture effect; never delivered to a desktop.", null, empty,
                CerebralJson.Element(snapshot), Caller: new(EventActorKind.User, "fixture-owner")));
            run = workflows.TryClaim(run.Id)!;
        }
        run = ClaimForStep(workflows, run);
        var arguments = CerebralJson.Element(new WorkerWorkflowEffects.ToolArguments(call.Name, call.Arguments));
        if (run.Effect is { State: WorkflowEffectState.Dispatched } prior &&
            JsonElement.DeepEquals(prior.Arguments, arguments))
            return context with { EffectId = prior.Id };
        if (run.Effect is { State: WorkflowEffectState.Dispatched } previous)
            run = workflows.CompleteEffect(run.Id, previous.Id, new(WorkflowEffectState.Succeeded, empty));
        run = ClaimForStep(workflows, run);
        var descriptor = new WorkflowCommandDescriptor(WorkerWorkflowEffects.ToolCommand, 1,
            "Isolated dispatched presentation metadata.", empty, empty, []);
        run = workflows.ApplyStep(run.Id, run.Revision,
            new(empty, new InvokeWorkflowCommand(descriptor.Id, descriptor.Version, arguments)), descriptor);
        run = workflows.DispatchEffect(run.Id, run.Revision);
        return context with { EffectId = run.Effect!.Id };
    }

    internal static InterfaceRequest Present(InterfaceRequests requests, StateStore store,
        ToolExecutionContext context, string kind, string path, string? fingerprint = null)
    {
        var call = kind == "plugin" ? new ToolCall(InterfaceToolExtension.PluginToolName,
            fingerprint is null ? CerebralJson.Element(new { path }) :
                CerebralJson.Element(new { path, expectedPackageFingerprint = fingerprint })) :
            new ToolCall(InterfaceToolExtension.ToolName, CerebralJson.Element(new { kind, path }));
        return requests.CreateForWorkflow(Live(store, context, call), kind, path, fingerprint);
    }

    internal static Task<ToolResult> Execute(InterfaceToolExtension tool, StateStore store,
        ToolCall call, ToolExecutionContext context, CancellationToken token) =>
        tool.ExecuteAsync(call, Live(store, context, call), token);

    internal static ToolExecutionContext ApprovedQa(StateStore store, ToolExecutionContext owner,
        InterfaceRequest presentation)
    {
        var workflows = new WorkflowStore(new EventJournal(Path.Combine(store.DirectoryPath, "engine.db")));
        var original = workflows.Get(owner.JobId).Submission;
        var id = presentation.PluginTests!.RequestId;
        var stage = workflows.Submit(original with { RequestId = id, ConversationSequence = 0,
            UserText = PluginContinuationGoal.TestStageText,
            Caller = new(EventActorKind.Service, "cerebral.plugin-tests") });
        presentation.PluginTests = presentation.PluginTests with { Status = "admitted", RunId = stage.Id };
        store.Put("interface", presentation.Id, presentation);
        return owner with { JobId = stage.Id, EffectId = Guid.NewGuid().ToString("N") };
    }

    internal static InterfaceRequest RetainedPresentation(InterfaceRequests requests, StateStore store,
        ToolExecutionContext context, string kind, string path, string? expectedFingerprint = null)
    {
        Metadata(store, context);
        var validated = kind == "plugin"
            ? Cerebral.Tools.InterfacePathPolicy.ValidateNativePluginPresentation(
                WorkflowUiOrigin.ResolveRetainedPresentation(store, new InterfaceRequest
                { Source = "workflow", SessionId = context.SessionId, JobId = context.JobId,
                    ProfileId = context.Profile.Id, Kind = kind, Path = path }, context.FullAccess), path)
            : Cerebral.Tools.InterfacePathPolicy.ValidatePresentation(context.Profile, kind, path, context.FullAccess);
        var fingerprint = kind == "plugin" ? NativeDesktopPluginPackage.ComputeFingerprint(validated.Path) : null;
        if (expectedFingerprint is not null && expectedFingerprint != fingerprint)
            throw new InvalidOperationException("Fixture package fingerprint changed.");
        var existing = requests.List(context.SessionId, 100).FirstOrDefault(item =>
            item.JobId == context.JobId && item.Kind == kind && item.Path == validated.Path &&
            item.PackageFingerprint == fingerprint);
        if (existing is not null) return existing;
        // This is an explicitly retained owner-review fixture, not an assertion that a live
        // presentation tool ran. Claim/complete still cross the actual production boundaries.
        var request = new InterfaceRequest { Source = "workflow", SessionId = context.SessionId,
            JobId = context.JobId, ProfileId = context.Profile.Id, Kind = kind, Path = validated.Path,
            GeneratedSourceRoot = validated.GeneratedSourceRoot, PackageFingerprint = fingerprint };
        store.Put("interface", request.Id, request);
        return request;
    }

    internal static InterfaceRequest SeedPresentation(StateStore store, ToolExecutionContext context,
        string kind, string path, bool claimed = false)
    {
        Metadata(store, context);
        var request = new InterfaceRequest { Source = "workflow", SessionId = context.SessionId,
            JobId = context.JobId, ProfileId = context.Profile.Id, Kind = kind, Path = path,
            PackageFingerprint = kind == "plugin" ? NativeDesktopPluginPackage.ComputeFingerprint(path) : null,
            Status = claimed ? "claimed" : "pending", ClaimId = claimed ? Guid.NewGuid().ToString("N") : null,
            ClaimedAt = claimed ? DateTimeOffset.UtcNow : null };
        // Exact retained owner-review input for continuation/control fixtures. This deliberately
        // does not claim that a presentation tool or a desktop process was executed.
        store.Put("interface", request.Id, request);
        return request;
    }

    internal static WorkflowRun Finish(WorkflowStore workflows, string id)
    {
        var run = workflows.Get(id);
        if (run.IsTerminal) return run;
        if (run.CancelRequested || run.Status is not (WorkflowStatus.Queued or WorkflowStatus.Running) ||
            run.Effect?.State == WorkflowEffectState.Unknown)
            throw new InvalidOperationException("Fixture cannot finish a stopped or uncertain workflow.");
        if (run.Effect is { State: WorkflowEffectState.Dispatched } effect)
            run = workflows.CompleteEffect(run.Id, effect.Id,
                new(WorkflowEffectState.Succeeded, WorkflowContract.EmptyObject));
        run = ClaimForStep(workflows, run);
        return workflows.ApplyStep(run.Id, run.Revision,
            new(WorkflowContract.EmptyObject, new CompleteWorkflow(WorkflowContract.EmptyObject)));
    }

    private static WorkflowRun ClaimForStep(WorkflowStore workflows, WorkflowRun run)
    {
        // A confirmed effect returns the Engine to Queued. Reclaim its new revision
        // before another decision; waiting/uncertain/cancelled runs are never resumed here.
        if (run.Status == WorkflowStatus.Queued)
            run = workflows.TryClaim(run.Id) ?? throw new InvalidOperationException("Fixture run could not be reclaimed.");
        if (run.Status != WorkflowStatus.Running || run.CancelRequested)
            throw new InvalidOperationException("Fixture step requires an uncancelled running workflow.");
        return run;
    }

    private static void FinishPrevious(WorkflowStore workflows, string conversation)
    {
        foreach (var active in workflows.ListActive().Where(run => run.Submission.ConversationId == conversation))
            Finish(workflows, active.Id);
    }
}
