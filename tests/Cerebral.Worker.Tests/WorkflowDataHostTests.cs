using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Workflows;
using Cerebral.Tools;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Real host admission/effect persistence with deterministic fake tools and an unused
/// model adapter. All state belongs to a temporary fixture; no installed provider or GPU is used.</summary>
public static class WorkflowDataHostTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Workflow data host: " + label);
            checks++; Console.WriteLine("PASS: " + label);
        }
        void Reject(Action action, string label)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or
                InvalidOperationException or InvalidDataException or KeyNotFoundException)
            { Check(true, label); return; }
            throw new InvalidOperationException("Workflow data host accepted invalid input: " + label);
        }

        using var fixture = new Fixture();
        var source = await fixture.SourceAsync("source");
        var foreign = await fixture.SourceAsync("foreign-source", fixture.ForeignProfile, Fixture.ForeignProject);
        var unbound = await fixture.SourceAsync("unbound-source", projectId: null);
        var edition = Edition();
        var published = fixture.Host.RegisterWorkflowData(new(source.Id, edition), fixture.Profile);
        Check(published.Created && published.Entry.ProjectId == Fixture.Project &&
            published.Entry.Hash == WorkflowDataCatalog.Hash(edition),
            "publication binds the existing source project and exact immutable content hash");
        var duplicate = fixture.Host.RegisterWorkflowData(new(source.Id, edition), fixture.Profile);
        Check(!duplicate.Created && duplicate.Entry.Hash == published.Entry.Hash,
            "identical publication is idempotent without running a stage");
        Reject(() => fixture.Host.RegisterWorkflowData(new(source.Id, edition with { Description = "Different same-version data" }), fixture.Profile),
            "changed content at the same workflow ID/version cannot replace the accepted edition");
        Reject(() => fixture.Host.RegisterWorkflowData(new(foreign.Id, edition), fixture.Profile),
            "a profile cannot publish through another profile's source assignment");
        Reject(() => fixture.Host.RegisterWorkflowData(new(unbound.Id, edition), fixture.Profile),
            "a source without an existing project cannot supply workflow authority");
        var foreignEdition = edition with { Description = "Foreign project edition" };
        var foreignEntry = fixture.Host.RegisterWorkflowData(new(foreign.Id, foreignEdition), fixture.ForeignProfile).Entry;
        Check(fixture.Host.ListWorkflowData(new(source.Id), fixture.Profile).Entries.Single().Hash == published.Entry.Hash &&
            fixture.Host.ListWorkflowData(new(foreign.Id), fixture.ForeignProfile).Entries.Single().Hash == foreignEntry.Hash,
            "catalog listing uses source-owned project scope rather than a global edition index");
        Reject(() => fixture.Host.GetWorkflowData(new(source.Id, edition.Id, edition.Version, foreignEntry.Hash), fixture.Profile),
            "a foreign project hash cannot select another project's same ID/version");
        Check(fixture.Host.GetWorkflowData(new(source.Id, edition.Id, edition.Version, published.Entry.Hash), fixture.Profile).Hash == published.Entry.Hash,
            "exact get returns the selected source-project edition");
        Check(fixture.Tools.Dispatches == 0 && fixture.Models.Preparations == 0,
            "register/list/get perform no tool effect or model preparation");

        var request = fixture.Request("frozen-run", source, published.Entry);
        var admitted = fixture.Host.Submit(request, fixture.Profile);
        var sourceSnapshot = WorkerExecutionSnapshot.Read(source.Submission.SettingsSnapshot);
        var runSnapshot = WorkerExecutionSnapshot.Read(admitted.Submission.SettingsSnapshot);
        Check(admitted.Submission.ProjectId == source.Submission.ProjectId &&
            admitted.Submission.ProfileId == source.Submission.ProfileId && admitted.Submission.ParentRunId is null &&
            admitted.Submission.ConversationId == WorkerWorkflows.WorkflowDataConversationId(admitted.Id) &&
            admitted.Submission.ConversationId != source.Submission.ConversationId,
            "a data run gets its exact derived execution conversation while inheriting source project/profile without delegation roles");
        Check(JsonElement.DeepEquals(Data(sourceSnapshot.Profile), Data(runSnapshot.Profile)) &&
            JsonElement.DeepEquals(Data(sourceSnapshot.Tools), Data(runSnapshot.Tools)) &&
            JsonElement.DeepEquals(Data(sourceSnapshot.CortexScope), Data(runSnapshot.CortexScope)) &&
            runSnapshot.RoutingFingerprint == sourceSnapshot.RoutingFingerprint && runSnapshot.FullAccess == sourceSnapshot.FullAccess,
            "data admission preserves accepted profile/tool/root/Cortex and routing authority");
        Check(runSnapshot.PackageBindings.Select(binding => binding.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals([WorkflowDataModule.ModuleId, WorkerWorkflowEffects.ModuleId]) &&
            admitted.Submission.SettingsSnapshot.GetProperty("workflowData").GetProperty("hash").GetString() == published.Entry.Hash,
            "only the host-selected data/host package graph and complete exact edition are added");
        Check(fixture.Host.Submit(request, fixture.Profile).Revision == admitted.Revision,
            "lost-response resubmission retrieves the same accepted execution");
        Reject(() => fixture.Host.Submit(request with { Input = Data(new WorkflowDataRunInput(source.Id,
            edition.Id, edition.Version, published.Entry.Hash, Data(new { path = Path.Combine(fixture.AllowedRoot, "changed.txt") }))) }, fixture.Profile),
            "a stable request ID cannot be reused for changed workflow input");
        Reject(() => fixture.Host.Submit(fixture.Request("wrong-conversation", source, published.Entry) with
            { ConversationId = source.Submission.ConversationId }, fixture.Profile),
            "data admission rejects reuse of the active Chat conversation");
        Reject(() => fixture.Host.Submit(fixture.Request("foreign-run", foreign, foreignEntry) with
            { ProfileId = fixture.Profile.Id }, fixture.Profile),
            "a run cannot substitute another source profile or project");

        fixture.Profile.Instructions = "Current instructions must not replace accepted instructions.";
        fixture.Profile.MaxSteps = 128; fixture.Profile.MaxSeconds = 900;
        fixture.Profile.AllowedTools.Add("file.search"); fixture.Profile.Capabilities.Add("filesystem.search");
        fixture.SaveProfile(); fixture.Tools.ExposeSearch = true;
        var widened = fixture.Host.Submit(fixture.Request("accepted-limits", source, published.Entry), fixture.Profile);
        Check(widened.Submission.PolicyStepLimit <= sourceSnapshot.Profile.MaxSteps &&
            widened.Submission.ActiveTimeLimitSeconds <= sourceSnapshot.Profile.MaxSeconds &&
            WorkerExecutionSnapshot.Read(widened.Submission.SettingsSnapshot).Profile.Instructions == sourceSnapshot.Profile.Instructions,
            "later profile budget/instruction increases do not widen the source's accepted execution");
        var uncaptured = edition with { Id = "uncaptured", Stages = [new("first", "file.search", Data(new { path = "fixture" }))],
            RequiredCapabilities = ["filesystem.search"], Output = Data(new { first = "none", last = "none" }) };
        Reject(() => fixture.Host.RegisterWorkflowData(new(source.Id, uncaptured), fixture.Profile),
            "a newly granted tool remains absent from the source's captured action catalog");

        var versionTwo = edition with { Version = "2.0.0", Description = "Later immutable edition",
            Output = Data(new { first = "version two", last = "version two" }) };
        fixture.Host.RegisterWorkflowData(new(source.Id, versionTwo), fixture.Profile);
        var claimed = fixture.Host.Store.TryClaim(admitted.Id)!;
        var firstStep = new WorkflowDataModule().Workflows.Single().Advance(claimed);
        var toolDescriptor = fixture.Effects.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.ToolCommand).Descriptor;
        var prepared = fixture.Host.Store.ApplyStep(claimed.Id, claimed.Revision, firstStep, toolDescriptor);
        var preparedId = prepared.Effect!.Id;
        fixture.Restart(); fixture.Host.Store.RecoverInterrupted();
        var before = fixture.Tools.Dispatches;
        var completed = await fixture.Host.Runtime.RunAsync(admitted.Id);
        Check(completed.Status == WorkflowStatus.Completed && fixture.Tools.Dispatches == before + 2 &&
            completed.Result?.GetProperty("first").GetString() == "fixture-read:fixture.txt" &&
            completed.Result?.GetProperty("last").GetString() == "fixture-read:fixture.txt",
            "restart executes the frozen older edition through the real host effect bridge");
        Check(fixture.Tools.Contexts.Skip(before).First().EffectId == preparedId &&
            completed.Submission.SettingsSnapshot.GetProperty("workflowData").GetProperty("edition").GetProperty("version").GetString() == "1.0.0",
            "a prepared effect keeps its committed ID and exact edition across restart");
        Check((await fixture.Host.Runtime.RunAsync(completed.Id)).Revision == completed.Revision && fixture.Tools.Dispatches == before + 2,
            "reopening a completed run never repeats its effects");
        Reject(() => fixture.Host.RegisterWorkflowData(new(completed.Id, edition), fixture.Profile),
            "a data execution cannot publish recursively through its own source context");
        Reject(() => fixture.Host.Submit(fixture.Request("recursive-run", completed, published.Entry), fixture.Profile),
            "a data execution cannot become the source for another data run");

        var revoked = fixture.Host.Submit(fixture.Request("revoked-tool", source, published.Entry), fixture.Profile);
        var savedTools = fixture.Profile.AllowedTools.ToList(); fixture.Profile.AllowedTools.Clear(); fixture.SaveProfile();
        before = fixture.Tools.Dispatches;
        var failed = await fixture.Host.Runtime.RunAsync(revoked.Id);
        Check(failed.Status == WorkflowStatus.Failed && fixture.Tools.Dispatches == before,
            "a captured tool revoked after admission cannot dispatch the first stage");
        fixture.Profile.AllowedTools = savedTools; fixture.SaveProfile();
        var revokedCapability = fixture.Host.Submit(fixture.Request("revoked-capability", source, published.Entry), fixture.Profile);
        fixture.Profile.Capabilities.Remove("filesystem.read"); fixture.SaveProfile();
        before = fixture.Tools.Dispatches;
        failed = await fixture.Host.Runtime.RunAsync(revokedCapability.Id);
        Check(failed.Status == WorkflowStatus.Failed && fixture.Tools.Dispatches == before,
            "a current capability revocation blocks a captured stage without a provider effect");
        fixture.Profile.Capabilities.Add("filesystem.read"); fixture.SaveProfile();
        var changedDescriptor = fixture.Host.Submit(fixture.Request("changed-descriptor", source, published.Entry), fixture.Profile);
        fixture.Tools.ChangedDescriptor = true; before = fixture.Tools.Dispatches;
        failed = await fixture.Host.Runtime.RunAsync(changedDescriptor.Id);
        Check(failed.Status == WorkflowStatus.Failed && fixture.Tools.Dispatches == before,
            "a changed current tool descriptor cannot replace the source's accepted descriptor");
        fixture.Tools.ChangedDescriptor = false;

        var rootRevocation = fixture.Host.Submit(fixture.Request("revoked-root", source, published.Entry), fixture.Profile);
        fixture.Tools.FullAccessActive = true;
        fixture.Tools.AfterDispatch = () => { fixture.Profile.AllowedRoots.Clear(); fixture.SaveProfile(); };
        before = fixture.Tools.Dispatches;
        failed = await fixture.Host.Runtime.RunAsync(rootRevocation.Id);
        fixture.Tools.AfterDispatch = null;
        Check(failed.Status == WorkflowStatus.Failed && fixture.Tools.Dispatches == before + 1 &&
            failed.State.GetProperty("results").TryGetProperty("first", out _) &&
            fixture.Tools.LastAttempt is { EnforceProfileScope: true, FullAccess: false } last && last.Profile.AllowedRoots.Count == 0,
            "root revocation between stages retains prior evidence and later Full Access cannot widen the accepted scope");
        fixture.Profile.AllowedRoots = [fixture.AllowedRoot]; fixture.SaveProfile(); fixture.Tools.FullAccessActive = false;

        var uncertain = fixture.Host.Submit(fixture.Request("unknown-stage", source, published.Entry), fixture.Profile);
        fixture.Tools.Unknown = true; before = fixture.Tools.Dispatches;
        uncertain = await fixture.Host.Runtime.RunAsync(uncertain.Id);
        Check(uncertain.Status == WorkflowStatus.Waiting && uncertain.Effect?.State == WorkflowEffectState.Unknown &&
            fixture.Tools.Dispatches == before + 1 &&
            uncertain.Effect.Result?.Deserialize<WorkerWorkflowEffects.ToolData>(CerebralJson.Options)?.Evidence?.Outcome == ToolExecutionOutcome.Unknown,
            "an unconfirmed first stage remains waiting with its exact persisted Unknown evidence");
        var unknownRevision = uncertain.Revision;
        fixture.Restart(); fixture.Host.Store.RecoverInterrupted();
        Check((await fixture.Host.Runtime.RunAsync(uncertain.Id)).Revision == unknownRevision && fixture.Tools.Dispatches == before + 1,
            "restart and a later runtime call do not replay an uncertain stage or advance its successor");
        fixture.Tools.Unknown = false;

        var settings = fixture.Configuration.Get(); settings.QueueCapacity = fixture.Host.Store.CountActive(); fixture.Configuration.Save(settings);
        before = fixture.Tools.Dispatches;
        Reject(() => fixture.Host.Submit(fixture.Request("queue-full", source, published.Entry), fixture.Profile),
            "data submission respects the ordinary bounded host queue");
        Check(fixture.Host.Store.TryGet("queue-full") is null && fixture.Tools.Dispatches == before,
            "queue rejection persists no execution and dispatches no stage");
        settings = fixture.Configuration.Get(); settings.QueueCapacity = 200; fixture.Configuration.Save(settings);

        fixture.Profile.MaxSteps = 1; fixture.Profile.MaxSeconds = 7; fixture.SaveProfile();
        var limitedSource = await fixture.SourceAsync("limited-source");
        fixture.Profile.MaxSteps = 128; fixture.Profile.MaxSeconds = 900; fixture.SaveProfile();
        var limited = fixture.Host.Submit(fixture.Request("limited-run", limitedSource, published.Entry), fixture.Profile);
        Check(limited.Submission.EffectiveStepLimit == 1 && limited.Submission.ActiveTimeLimitSeconds <= 7,
            "data execution retains the source's small accepted step/time envelope despite current increases");
        before = fixture.Tools.Dispatches;
        failed = await fixture.Host.Runtime.RunAsync(limited.Id);
        Check(failed.Status == WorkflowStatus.Failed && fixture.Tools.Dispatches == before + 1 &&
            failed.Error?.Contains("step budget", StringComparison.OrdinalIgnoreCase) == true,
            "exhausted accepted transition budget prevents a later stage and cannot report successful output");

        settings = fixture.Configuration.Get(); settings.ExternalCommandsEnabled = !settings.ExternalCommandsEnabled; fixture.Configuration.Save(settings);
        Reject(() => fixture.Host.Submit(fixture.Request("routing-changed", source, published.Entry), fixture.Profile),
            "changed host execution routing cannot redirect a source-bound workflow");
        settings.ExternalCommandsEnabled = !settings.ExternalCommandsEnabled; fixture.Configuration.Save(settings);

        fixture.Profile.MaxSteps = 8; fixture.Profile.MaxSeconds = 60; fixture.SaveProfile();
        var spentSource = await fixture.SourceAsync("spent-source");
        var exhaustedSource = await fixture.SourceAsync("exhausted-time-source");
        var failedSource = await fixture.SourceAsync("failed-source");
        var remainingFailuresSource = await fixture.SourceAsync("remaining-failures-source", failureLimit: 3);
        var staleUnknownSource = await fixture.SourceAsync("stale-unknown-source");
        var staleFailedSource = await fixture.SourceAsync("stale-failed-time-source", failureLimit: 3);
        WorkflowRun RecordPriorOutcome(WorkflowRun owner, int elapsedMilliseconds, bool failedOutcome, bool unknownOutcome = false)
        {
            var active = fixture.Host.Store.TryClaim(owner.Id)!;
            active = fixture.Host.Store.ApplyStep(active.Id, active.Revision,
                new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand(WorkerWorkflowEffects.ToolCommand, 1,
                    Data(new WorkerWorkflowEffects.ToolArguments("file.read", Data(new
                        { path = Path.Combine(fixture.AllowedRoot, "fixture.txt") }), fixture.AllowedRoot)))), toolDescriptor);
            active = fixture.Host.Store.DispatchEffect(active.Id, active.Revision, 1000,
                failedOutcome ? WorkerWorkflowEffects.LaunchFailureBudget : null);
            return fixture.Host.Store.CompleteEffect(active.Id, active.Effect!.Id,
                new(unknownOutcome ? WorkflowEffectState.Unknown : failedOutcome ? WorkflowEffectState.Failed : WorkflowEffectState.Succeeded,
                    Data(new WorkerWorkflowEffects.ToolData("file.read", "Retained fixture prior outcome", false,
                        new(unknownOutcome ? ToolExecutionOutcome.Unknown : failedOutcome ? ToolExecutionOutcome.Failed : ToolExecutionOutcome.Succeeded, "fixture.tools", active.Effect.Id))),
                    failedOutcome ? "Fixture charged admitted failure allowance." : null), elapsedMilliseconds);
        }
        spentSource = RecordPriorOutcome(spentSource, 20_000, false);
        exhaustedSource = RecordPriorOutcome(exhaustedSource, 59_500, false);
        failedSource = RecordPriorOutcome(failedSource, 1000, true);
        remainingFailuresSource = RecordPriorOutcome(remainingFailuresSource, 1000, true);
        staleUnknownSource = RecordPriorOutcome(staleUnknownSource, 59_500, false, unknownOutcome: true);
        staleFailedSource = RecordPriorOutcome(staleFailedSource, 59_500, true);
        fixture.Profile.MaxSteps = 128; fixture.Profile.MaxSeconds = 900; fixture.SaveProfile();
        var remainingTime = fixture.Host.Submit(fixture.Request("remaining-time-run", spentSource, published.Entry), fixture.Profile);
        Check(remainingTime.Submission.ActiveTimeLimitSeconds == 40 && spentSource.ActiveMilliseconds == 20_000,
            "a child receives only the persisted source's remaining active time despite current profile increases");
        before = fixture.Tools.Dispatches;
        Reject(() => fixture.Host.Submit(fixture.Request("exhausted-time-run", exhaustedSource, published.Entry), fixture.Profile),
            "less than one second of source active time rejects a new execution");
        Reject(() => fixture.Host.Submit(fixture.Request("exhausted-failure-run", failedSource, published.Entry), fixture.Profile),
            "an exhausted admitted failure allowance cannot be reset by creating a child");
        Reject(() => fixture.Host.Submit(fixture.Request("stale-unknown-reservation-run", staleUnknownSource, published.Entry), fixture.Profile),
            "an Unknown effect's retained reservation cannot refund its measured source active time");
        Reject(() => fixture.Host.Submit(fixture.Request("stale-failed-reservation-run", staleFailedSource, published.Entry), fixture.Profile),
            "a completed failed effect's retained reservation cannot refund active time despite remaining failure allowance");
        var remainingFailures = fixture.Host.Submit(fixture.Request("remaining-failures-run", remainingFailuresSource, published.Entry), fixture.Profile);
        Check(remainingFailures.Submission.FailureLimits[WorkerWorkflowEffects.LaunchFailureBudget] == 2 &&
            remainingFailuresSource.FailedEffects[WorkerWorkflowEffects.LaunchFailureBudget] == 1 && fixture.Tools.Dispatches == before &&
            fixture.Host.Store.TryGet("exhausted-time-run") is null && fixture.Host.Store.TryGet("exhausted-failure-run") is null,
            "admission subtracts retained failed attempts without dispatching a tool or persisting rejected children");
        Check(staleUnknownSource.Effect is { State: WorkflowEffectState.Unknown, ReservedExecutionMilliseconds: 1000 } &&
            staleFailedSource.Effect is { State: WorkflowEffectState.Failed, ReservedExecutionMilliseconds: 1000 } &&
            staleUnknownSource.ActiveMilliseconds == 59_500 && staleFailedSource.ActiveMilliseconds == 59_500 &&
            fixture.Host.Store.TryGet("stale-unknown-reservation-run") is null && fixture.Host.Store.TryGet("stale-failed-reservation-run") is null,
            "stale reservation fixtures retain exact persisted outcome/time evidence and rejected children remain absent");

        var extension = new WorkflowDataToolExtension(() => fixture.Host);
        fixture.Tools.Extension = extension;
        fixture.Profile.AllowedTools.AddRange(extension.Tools.Select(tool => tool.Name));
        fixture.Profile.Capabilities.Add("diagnostics"); fixture.SaveProfile();
        await using var dispatcher = new ToolDispatcher(fixture.Configuration.Get, [extension]);
        async Task<(WorkflowRun Source, ToolExecutionContext Context)> Calling(string id, ToolCall call)
        {
            var owner = await fixture.SourceAsync(id);
            var active = fixture.Host.Store.TryClaim(owner.Id)!;
            var arguments = Data(new WorkerWorkflowEffects.ToolArguments(call.Name, call.Arguments, fixture.AllowedRoot));
            active = fixture.Host.Store.ApplyStep(active.Id, active.Revision,
                new(WorkflowContract.EmptyObject, new InvokeWorkflowCommand(WorkerWorkflowEffects.ToolCommand, 1, arguments)), toolDescriptor);
            active = fixture.Host.Store.DispatchEffect(active.Id, active.Revision, 60_000);
            var effective = WorkerExecutionSnapshot.EffectiveProfile(fixture.State,
                WorkerExecutionSnapshot.Read(owner.Submission.SettingsSnapshot), owner.Submission.ProfileId);
            var context = new ToolExecutionContext(effective, owner.Submission.ConversationId, owner.Id, fixture.AllowedRoot,
                EffectId: active.Effect!.Id) { EnforceProfileScope = true };
            return (active, context);
        }

        // Use the real dispatcher and effect adapter: a catalog mistake must reach the model
        // as a known failed receipt, without an invented child or an uncertainty hold.
        var semanticEffects = new WorkerWorkflowEffects(fixture.State, fixture.Configuration, fixture.Models,
            dispatcher, workflowHost: () => fixture.Host);
        var semanticTool = semanticEffects.Commands.Single(command => command.Descriptor.Id == WorkerWorkflowEffects.ToolCommand);
        var rejectedSelections = new[]
        {
            new ToolCall(WorkflowDataToolExtension.RunTool, Data(new
                { id = "missing-edition", version = edition.Version, hash = published.Entry.Hash,
                  input = new { path = Path.Combine(fixture.AllowedRoot, "fixture.txt") } })),
            new ToolCall(WorkflowDataToolExtension.RunTool, Data(new
                { id = edition.Id, version = edition.Version, hash = new string('0', 64),
                  input = new { path = Path.Combine(fixture.AllowedRoot, "fixture.txt") } })),
            new ToolCall(WorkflowDataToolExtension.RunTool, Data(new
                { id = edition.Id, version = edition.Version, hash = published.Entry.Hash,
                  input = new { unexpected = true } })),
            new ToolCall(WorkflowDataToolExtension.GetTool, Data(new
                { id = "missing-edition", version = edition.Version, hash = published.Entry.Hash }))
        };
        for (var selection = 0; selection < rejectedSelections.Length; selection++)
        {
            var rejectedCall = rejectedSelections[selection];
            var caller = await Calling("rejected-selection-" + selection, rejectedCall);
            before = fixture.Tools.Dispatches;
            var result = await semanticTool.ExecuteAsync(new WorkflowCommandContext(caller.Context.EffectId!,
                caller.Source.Submission, caller.Source.Effect!.Arguments) { RunId = caller.Source.Id }, default);
            var toolData = WorkerWorkflows.Read<WorkerWorkflowEffects.ToolData>(result.Data!.Value);
            using var diagnostic = JsonDocument.Parse(toolData.Content);
            Check(result.State == WorkflowEffectState.Failed &&
                toolData.Evidence is { Outcome: ToolExecutionOutcome.Rejected, ProviderId: "cerebral.workflow-data", JobId: null } &&
                toolData.Evidence.RequestId == caller.Context.EffectId &&
                diagnostic.RootElement.GetProperty("execution").GetString() == "no_workflow_dispatched" &&
                diagnostic.RootElement.GetProperty("hint").GetString()!.Contains("edition", StringComparison.Ordinal) &&
                fixture.Host.Store.TryGet(WorkflowDataToolExtension.RunId(caller.Context)) is null &&
                fixture.Tools.Dispatches == before,
                "catalog selection/input rejection has exact failed evidence and repair guidance without child admission (" + selection + ")");
            var persisted = fixture.Host.Store.CompleteEffect(caller.Source.Id, caller.Context.EffectId!, result);
            Check(persisted.Effect!.State == WorkflowEffectState.Failed &&
                WorkerWorkflows.Read<WorkflowCommandResult>(persisted.Signal.Data).State == WorkflowEffectState.Failed &&
                persisted.Status != WorkflowStatus.Waiting,
                "known pre-admission rejection persists as Failed rather than an Unknown wait (" + selection + ")");
        }
        var discoveredCatalog = await dispatcher.ListAsync(fixture.Profile, default);
        Check(discoveredCatalog.Single(tool => tool.Name == WorkflowDataToolExtension.RunTool).PrerequisiteTools!
                .SequenceEqual([WorkflowDataToolExtension.ListTool, WorkflowDataToolExtension.GetTool]) &&
            discoveredCatalog.Single(tool => tool.Name == WorkflowDataToolExtension.GetTool).PrerequisiteTools!
                .SequenceEqual([WorkflowDataToolExtension.ListTool]) &&
            discoveredCatalog.Single(tool => tool.Name == WorkflowDataToolExtension.RegisterTool).PrerequisiteTools!
                .SequenceEqual([WorkflowDataToolExtension.GuideTool]),
            "discovered catalog tools advertise staged lookup/authoring references");
        var restrictedCatalogProfile = CerebralJson.Read<AgentProfile>(Data(fixture.Profile));
        restrictedCatalogProfile.AllowedTools.Remove(WorkflowDataToolExtension.GetTool);
        restrictedCatalogProfile.AllowedTools.Remove(WorkflowDataToolExtension.ListTool);
        var restrictedCatalog = await dispatcher.ListAsync(restrictedCatalogProfile, default);
        Check(restrictedCatalog.All(tool => tool.Name != WorkflowDataToolExtension.GetTool &&
                tool.Name != WorkflowDataToolExtension.ListTool) &&
            !restrictedCatalogProfile.AllowedTools.Contains(WorkflowDataToolExtension.GetTool) &&
            !restrictedCatalogProfile.AllowedTools.Contains(WorkflowDataToolExtension.ListTool),
            "prerequisite metadata does not expand catalog grants");

        var recoveryEdition = edition with { Id = "fixture.publication-recovery", Description = "Retained publication acknowledgement fixture" };
        var publishCall = new ToolCall(WorkflowDataToolExtension.RegisterTool, Data(new { edition = recoveryEdition }));
        var publishing = await Calling("publication-source", publishCall);
        before = fixture.Tools.Dispatches;
        var publication = await dispatcher.ExecuteAsync(publishCall, publishing.Context, default);
        using var publishedBody = JsonDocument.Parse(publication.Content);
        Check(publication.Success && publication.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, ProviderId: "cerebral.workflow-data", JobId: null } &&
            publication.Evidence.RequestId == publishing.Context.EffectId &&
            publishedBody.RootElement.GetProperty("status").GetString() == "created" && fixture.Tools.Dispatches == before,
            "semantic publication returns its durable effect identity without inventing a child run or dispatching a stage");
        // Simulate a lost acknowledgement after an actual successful publication. No storage fault
        // is injected: the recovery path receives the exact semantic receipt and persisted intent.
        var publicationUnknown = publication.Evidence! with { Outcome = ToolExecutionOutcome.Unknown, State = "unconfirmed" };
        var uncertainPublication = fixture.Host.Store.CompleteEffect(publishing.Source.Id, publishing.Context.EffectId!,
            new(WorkflowEffectState.Unknown,
                Data(new WorkerWorkflowEffects.ToolData(publishCall.Name, "Fixture lost publication acknowledgement", false, publicationUnknown)),
                "Simulated acknowledgement loss after confirmed fixture publication."));
        var publicationCount = fixture.Host.ListWorkflowData(new(publishing.Source.Id), fixture.Profile).Entries.Count;
        fixture.Profile.Capabilities.Remove("filesystem.read"); fixture.Profile.AllowedTools.Remove("file.read"); fixture.SaveProfile();
        var recoveredPublication = await dispatcher.ReconcileAsync(publishCall, publishing.Context, publicationUnknown, default);
        using var recoveredBody = JsonDocument.Parse(recoveredPublication.Content);
        Check(recoveredPublication.Success && recoveredPublication.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, ProviderId: "cerebral.workflow-data", JobId: null } &&
            recoveredPublication.Evidence.RequestId == publishing.Context.EffectId &&
            recoveredBody.RootElement.GetProperty("status").GetString() == "present" &&
            recoveredBody.RootElement.GetProperty("id").GetString() == recoveryEdition.Id &&
            recoveredBody.RootElement.GetProperty("version").GetString() == recoveryEdition.Version &&
            recoveredBody.RootElement.GetProperty("hash").GetString() == WorkflowDataCatalog.Hash(recoveryEdition) &&
            fixture.Host.Store.Get(publishing.Source.Id).Revision == uncertainPublication.Revision &&
            fixture.Host.ListWorkflowData(new(publishing.Source.Id), fixture.Profile).Entries.Count == publicationCount &&
            fixture.Tools.Dispatches == before && fixture.Tools.Reconciliations == 0,
            "simulated lost publication acknowledgement recovers by exact inspection even after stage authority is revoked, without republishing or dispatching");
        fixture.Profile.Capabilities.Add("filesystem.read"); fixture.Profile.AllowedTools.Add("file.read"); fixture.SaveProfile();
        foreach (var wrongPublicationReceipt in new[]
        {
            publicationUnknown with { ProviderId = "cerebral.plugin" },
            publicationUnknown with { ProviderId = "" },
            publicationUnknown with { RequestId = "foreign-effect" },
            publicationUnknown with { RequestId = null }
        })
            Check((await dispatcher.ReconcileAsync(publishCall, publishing.Context, wrongPublicationReceipt, default)).EffectiveOutcome == ToolExecutionOutcome.Unknown &&
                fixture.Tools.Dispatches == before, "missing or changed publication provider/effect identity remains Unknown without replay");
        Check((await dispatcher.ReconcileAsync(publishCall, publishing.Context, null, default)).EffectiveOutcome == ToolExecutionOutcome.Unknown &&
            fixture.Tools.Dispatches == before, "a publication with no prior semantic evidence cannot be reconstructed by replay");
        var changedPublicationCall = new ToolCall(WorkflowDataToolExtension.RegisterTool,
            Data(new { edition = recoveryEdition with { Description = "Changed recovery content" } }));
        Check((await dispatcher.ReconcileAsync(changedPublicationCall, publishing.Context, publicationUnknown, default)).EffectiveOutcome == ToolExecutionOutcome.Unknown &&
            fixture.Tools.Dispatches == before, "changed publication content cannot borrow a matching immutable publication acknowledgement");
        Check((await dispatcher.ReconcileAsync(publishCall, publishing.Context with { SessionId = "foreign-publication-chat" }, publicationUnknown, default)).EffectiveOutcome == ToolExecutionOutcome.Unknown &&
            fixture.Tools.Dispatches == before, "publication recovery cannot substitute another source conversation");
        var absentEdition = recoveryEdition with { Id = "fixture.unconfirmed-absent" };
        var absentPublicationCall = new ToolCall(WorkflowDataToolExtension.RegisterTool, Data(new { edition = absentEdition }));
        var absentPublishing = await Calling("absent-publication-source", absentPublicationCall);
        var absentEvidence = new ToolExecutionEvidence(ToolExecutionOutcome.Unknown, "cerebral.workflow-data", absentPublishing.Context.EffectId);
        Check((await dispatcher.ReconcileAsync(absentPublicationCall, absentPublishing.Context, absentEvidence, default)).EffectiveOutcome == ToolExecutionOutcome.Unknown &&
            fixture.Host.ListWorkflowData(new(absentPublishing.Source.Id), fixture.Profile).Entries.All(entry => entry.Id != absentEdition.Id) &&
            fixture.Tools.Dispatches == before,
            "a missing immutable publication remains Unknown and inspection never creates its absent edition");

        ToolCall RunCall(string? path = null) => new(WorkflowDataToolExtension.RunTool, Data(new
            { id = edition.Id, version = edition.Version, hash = published.Entry.Hash,
              input = new { path = path ?? Path.Combine(fixture.AllowedRoot, "fixture.txt") } }));

        var runCall = RunCall();
        fixture.Profile.MaxSeconds = 60; fixture.SaveProfile();
        var reservedOuter = await Calling("reserved-outer-source", runCall);
        var differentOuterCall = new ToolCall(WorkflowDataToolExtension.GetTool, Data(new
            { id = edition.Id, version = edition.Version, hash = published.Entry.Hash }));
        var differentOuter = await Calling("different-outer-source", differentOuterCall);
        fixture.Profile.MaxSeconds = 900; fixture.SaveProfile();
        var reservedId = WorkflowDataToolExtension.RunId(reservedOuter.Context);
        var exactReservedRequest = fixture.Request(reservedId, reservedOuter.Source, published.Entry) with
            { UserText = "Run reusable workflow " + edition.Id };
        before = fixture.Tools.Dispatches;
        Reject(() => fixture.Host.Submit(fixture.Request("foreign-outer-reservation-run", reservedOuter.Source, published.Entry), fixture.Profile),
            "a different child request cannot borrow an enclosing RunTool reservation");
        Reject(() => fixture.Host.Submit(exactReservedRequest with
            { Input = Data(new WorkflowDataRunInput(reservedOuter.Source.Id, edition.Id, edition.Version, published.Entry.Hash,
                Data(new { path = Path.Combine(fixture.AllowedRoot, "changed.txt") }))) }, fixture.Profile),
            "matching deterministic child identity cannot borrow an outer reservation for different frozen input");
        var differentId = WorkflowDataToolExtension.RunId(differentOuter.Context);
        Reject(() => fixture.Host.Submit(fixture.Request(differentId, differentOuter.Source, published.Entry), fixture.Profile),
            "an enclosing catalog read reservation cannot be refunded for a data execution");
        var reservedChild = fixture.Host.Submit(exactReservedRequest, fixture.Profile);
        Check(reservedOuter.Source.ActiveMilliseconds == 60_000 && reservedChild.Submission.ActiveTimeLimitSeconds == 60 &&
            fixture.Tools.Dispatches == before && fixture.Host.Store.TryGet("foreign-outer-reservation-run") is null &&
            fixture.Host.Store.TryGet(differentId) is null,
            "only the exact currently Dispatched RunTool identity and frozen input can recover its enclosing precharge");

        var semantic = await Calling("semantic-source", runCall);
        var derivedId = WorkflowDataToolExtension.RunId(semantic.Context);
        foreach (var forged in new[]
        {
            semantic.Context with { SessionId = "foreign-chat" },
            semantic.Context with { JobId = foreign.Id },
            semantic.Context with { Profile = fixture.ForeignProfile },
            semantic.Context with { EffectId = "forged-effect" },
            semantic.Context with { EnforceProfileScope = false },
            new ToolExecutionContext(fixture.Profile, "direct", "direct"),
            new ToolExecutionContext(fixture.Profile, "legacy-chat", "legacy-job", EffectId: "legacy-effect") { EnforceProfileScope = true }
        })
        {
            before = fixture.Tools.Dispatches;
            var rejection = await dispatcher.ExecuteAsync(runCall, forged, default);
            Check(!rejection.Success && fixture.Tools.Dispatches == before && fixture.Host.Store.TryGet(derivedId) is null,
                "forged/direct/legacy catalog context cannot admit a data execution (" + forged.JobId + "/" + forged.SessionId + ")");
        }
        var changedCall = RunCall(Path.Combine(fixture.AllowedRoot, "changed.txt"));
        Check(!(await dispatcher.ExecuteAsync(changedCall, semantic.Context, default)).Success && fixture.Host.Store.TryGet(derivedId) is null,
            "semantic arguments must equal the exact committed source tool effect");
        before = fixture.Tools.Dispatches;
        var receipt = await dispatcher.ExecuteAsync(runCall, semantic.Context, default);
        Check(receipt.Success && receipt.Evidence is { Outcome: ToolExecutionOutcome.Succeeded, ProviderId: "cerebral.workflow-data" } &&
            receipt.Evidence.RequestId == semantic.Context.EffectId && receipt.Evidence.JobId == derivedId &&
            fixture.Host.Store.Get(derivedId).Status == WorkflowStatus.Completed && fixture.Tools.Dispatches == before + 2,
            "the actual dispatcher extension seam executes one derived run and returns exact outer/child identities");
        var repeated = await dispatcher.ExecuteAsync(runCall, semantic.Context, default);
        Check(repeated.Success && repeated.Evidence?.JobId == derivedId && fixture.Tools.Dispatches == before + 2,
            "the same durable semantic effect retrieves its completed run without repeating stages");
        var unknownEvidence = receipt.Evidence! with { Outcome = ToolExecutionOutcome.Unknown };
        fixture.Host.Store.CompleteEffect(semantic.Source.Id, semantic.Context.EffectId!, new(WorkflowEffectState.Unknown,
            Data(new WorkerWorkflowEffects.ToolData(runCall.Name, receipt.Content, receipt.Truncated, unknownEvidence)),
            "Fixture lost outer confirmation; child completion is retained.", derivedId));
        var reconciled = await dispatcher.ReconcileAsync(runCall, semantic.Context, unknownEvidence, default);
        Check(reconciled.Success && reconciled.Evidence?.JobId == derivedId && reconciled.Evidence.RequestId == semantic.Context.EffectId &&
            fixture.Tools.Dispatches == before + 2 && fixture.Tools.Reconciliations == 0,
            "outer Unknown reconciliation inspects the retained completed child without executing or reconciling any stage provider");
        foreach (var wrongReceipt in new[]
        {
            unknownEvidence with { ProviderId = "foreign.provider" },
            unknownEvidence with { RequestId = "foreign-effect" },
            unknownEvidence with { JobId = "foreign-run" }
        })
            Check((await dispatcher.ReconcileAsync(runCall, semantic.Context, wrongReceipt, default)).EffectiveOutcome == ToolExecutionOutcome.Unknown &&
                fixture.Tools.Dispatches == before + 2, "foreign outer receipt coordinates cannot establish completion or replay a stage");
        Check((await dispatcher.ReconcileAsync(changedCall, semantic.Context, unknownEvidence, default)).EffectiveOutcome == ToolExecutionOutcome.Unknown &&
            fixture.Tools.Dispatches == before + 2, "changed recovery arguments remain Unknown instead of reusing another child's result");

        foreach (var state in new[] { WorkflowStatus.Queued, WorkflowStatus.Running, WorkflowStatus.Waiting, WorkflowStatus.Failed, WorkflowStatus.Cancelled })
        {
            var id = "status-" + state.ToString().ToLowerInvariant();
            var statusCall = new ToolCall(WorkflowDataToolExtension.StatusTool, Data(new { runId = id, includeOutput = true }));
            var statusOwner = await Calling(id + "-source", statusCall);
            var child = fixture.Host.Submit(fixture.Request(id, statusOwner.Source, published.Entry), fixture.Profile);
            child = state switch
            {
                WorkflowStatus.Running => fixture.Host.Store.TryClaim(child.Id)!,
                WorkflowStatus.Waiting => fixture.Host.Store.Hold(child.Id, child.Revision, "Fixture held without an effect."),
                WorkflowStatus.Failed => fixture.Host.Store.Fail(child.Id, child.Revision, "Fixture failed before a stage."),
                WorkflowStatus.Cancelled => fixture.Host.Store.Cancel(child.Id),
                _ => child
            };
            before = fixture.Tools.Dispatches;
            var status = await dispatcher.ExecuteAsync(statusCall, statusOwner.Context, default);
            using var statusBody = JsonDocument.Parse(status.Content);
            Check(!status.Success && status.Evidence?.State == state.ToString() && status.Evidence.JobId == child.Id &&
                statusBody.RootElement.GetProperty("status").GetString() == state.ToString() &&
                statusBody.RootElement.GetProperty("output").ValueKind == JsonValueKind.Null && fixture.Tools.Dispatches == before,
                "status preserves " + state + " as a non-successful exact read with no stage dispatch");
            if (state == WorkflowStatus.Running) fixture.Host.Store.Cancel(child.Id);
        }

        var otherStatusCall = new ToolCall(WorkflowDataToolExtension.StatusTool, Data(new { runId = derivedId }));
        var otherStatusOwner = await Calling("foreign-status-source", otherStatusCall);
        before = fixture.Tools.Dispatches;
        Check(!(await dispatcher.ExecuteAsync(otherStatusCall, otherStatusOwner.Context, default)).Success && fixture.Tools.Dispatches == before,
            "a same-profile status tool cannot inspect a child from a different source assignment");

        var unbudgetedFailureSource = await Calling("unbudgeted-failure-source", runCall);
        var unbudgetedFailureId = WorkflowDataToolExtension.RunId(unbudgetedFailureSource.Context);
        var unbudgetedFailure = fixture.Host.Submit(fixture.Request(unbudgetedFailureId, unbudgetedFailureSource.Source, published.Entry) with
            { UserText = "Run reusable workflow " + edition.Id }, unbudgetedFailureSource.Context.Profile);
        var failureTools = fixture.Profile.AllowedTools.ToList();
        fixture.Profile.AllowedTools.Remove("file.read"); fixture.SaveProfile();
        before = fixture.Tools.Dispatches;
        try { unbudgetedFailure = await fixture.Host.Runtime.RunAsync(unbudgetedFailure.Id); }
        finally { fixture.Profile.AllowedTools = failureTools; fixture.SaveProfile(); }
        Check(unbudgetedFailure.Status == WorkflowStatus.Failed && unbudgetedFailure.FailedEffects.Count == 0 &&
            unbudgetedFailure.Signal.Kind == WorkflowSignalKind.EffectResult &&
            unbudgetedFailure.Signal.Data.Deserialize<WorkflowCommandResult>(CerebralJson.Options)?.State == WorkflowEffectState.Failed &&
            fixture.Tools.Dispatches == before,
            "a real host file-stage admission failure retains Failed outcome without a launch-budget counter or provider dispatch");
        var failedReceipt = await dispatcher.ExecuteAsync(runCall, unbudgetedFailureSource.Context, default);
        using var failedReceiptBody = JsonDocument.Parse(failedReceipt.Content);
        Check(!failedReceipt.Success && failedReceipt.Evidence is { Outcome: ToolExecutionOutcome.Failed, ProviderId: "cerebral.workflow-data", State: "Failed" } &&
            failedReceipt.Evidence.JobId == unbudgetedFailureId && failedReceipt.Evidence.RequestId == unbudgetedFailureSource.Context.EffectId &&
            failedReceiptBody.RootElement.GetProperty("failedCommands").GetInt32() == 1 &&
            failedReceiptBody.RootElement.GetProperty("completedCommands").GetInt32() == 0 && fixture.Tools.Dispatches == before,
            "the actual semantic receipt counts the retained failed command independently of empty failure-budget counters");
        var failedOuterUnknown = failedReceipt.Evidence! with { Outcome = ToolExecutionOutcome.Unknown };
        fixture.Host.Store.CompleteEffect(unbudgetedFailureSource.Source.Id, unbudgetedFailureSource.Context.EffectId!,
            new(WorkflowEffectState.Unknown,
                Data(new WorkerWorkflowEffects.ToolData(runCall.Name, "Fixture lost failed-child acknowledgement", false, failedOuterUnknown)),
                "Simulated outer acknowledgement loss after retained fixture failure.", unbudgetedFailureId));
        var recoveredFailure = await dispatcher.ReconcileAsync(runCall, unbudgetedFailureSource.Context, failedOuterUnknown, default);
        using var recoveredFailureBody = JsonDocument.Parse(recoveredFailure.Content);
        Check(!recoveredFailure.Success && recoveredFailure.Evidence is { Outcome: ToolExecutionOutcome.Failed, State: "Failed" } &&
            recoveredFailure.Evidence.JobId == unbudgetedFailureId && recoveredFailure.Evidence.RequestId == unbudgetedFailureSource.Context.EffectId &&
            recoveredFailureBody.RootElement.GetProperty("failedCommands").GetInt32() == 1 &&
            recoveredFailureBody.RootElement.GetProperty("output").ValueKind == JsonValueKind.Null &&
            (await fixture.Host.Runtime.RunAsync(unbudgetedFailureId)).Revision == unbudgetedFailure.Revision &&
            fixture.Host.Store.Get(unbudgetedFailureId).FailedEffects.Count == 0 && fixture.Tools.Dispatches == before && fixture.Tools.Reconciliations == 0,
            "exact recovery preserves one unbudgeted failed command and no output without repeating or reconciling a provider effect");

        var racing = await Calling("racing-source", runCall);
        var racingId = WorkflowDataToolExtension.RunId(racing.Context);
        fixture.Host.Submit(fixture.Request(racingId, racing.Source, published.Entry) with
            { UserText = "Run reusable workflow " + edition.Id }, racing.Context.Profile);
        fixture.Tools.DispatchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Tools.DispatchRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        before = fixture.Tools.Dispatches;
        var competingRunner = fixture.Host.Runtime.RunAsync(racingId);
        Task<ToolResult>? inspectingRunner = null;
        try
        {
            await fixture.Tools.DispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            inspectingRunner = dispatcher.ExecuteAsync(runCall, racing.Context, default);
            Check(!inspectingRunner.IsCompleted && fixture.Host.Store.Get(racingId).Status == WorkflowStatus.Running,
                "a competing persisted claim makes the outer run inspect the existing runner instead of executing another effect");
            fixture.Tools.DispatchRelease.TrySetResult();
            var competed = await competingRunner.WaitAsync(TimeSpan.FromSeconds(5));
            var racingReceipt = await inspectingRunner.WaitAsync(TimeSpan.FromSeconds(5));
            Check(competed.Status == WorkflowStatus.Completed && racingReceipt.Success && racingReceipt.Evidence?.JobId == racingId &&
                fixture.Tools.Dispatches == before + 2, "competing runner and outer status wait settle one child with each stage dispatched exactly once");
        }
        finally
        {
            fixture.Tools.DispatchRelease.TrySetResult();
            await competingRunner.WaitAsync(TimeSpan.FromSeconds(5));
            if (inspectingRunner is not null) await inspectingRunner.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Tools.DispatchEntered = null; fixture.Tools.DispatchRelease = null;
        }

        var cancelledSource = await Calling("cancelled-competing-source", runCall);
        var cancelledId = WorkflowDataToolExtension.RunId(cancelledSource.Context);
        fixture.Host.Submit(fixture.Request(cancelledId, cancelledSource.Source, published.Entry) with
            { UserText = "Run reusable workflow " + edition.Id }, cancelledSource.Context.Profile);
        fixture.Tools.DispatchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Tools.DispatchRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        before = fixture.Tools.Dispatches;
        var cancelledRunner = fixture.Host.Runtime.RunAsync(cancelledId);
        Task<ToolResult>? cancelledInspector = null;
        try
        {
            await fixture.Tools.DispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var firstEffectId = fixture.Host.Store.Get(cancelledId).Effect!.Id;
            cancelledInspector = dispatcher.ExecuteAsync(runCall, cancelledSource.Context, default);
            Check(!cancelledInspector.IsCompleted && fixture.Host.Store.Get(cancelledId).Status == WorkflowStatus.Running,
                "source cancellation fixture has an independently claimed child and an inspecting outer caller");
            var sourceCancellation = fixture.Host.Store.Cancel(cancelledSource.Source.Id);
            Check(sourceCancellation.CancelRequested && sourceCancellation.Status == WorkflowStatus.Cancelling &&
                fixture.Host.Store.Get(cancelledId).Effect is { State: WorkflowEffectState.Dispatched } executing && executing.Id == firstEffectId,
                "source cancellation preserves the already Dispatched stage's exact durable identity while it settles");
            fixture.Tools.DispatchRelease.TrySetResult();
            var cancelledChild = await cancelledRunner.WaitAsync(TimeSpan.FromSeconds(5));
            var cancellationReceipt = await cancelledInspector.WaitAsync(TimeSpan.FromSeconds(5));
            var firstEvidence = cancelledChild.State.GetProperty("results").GetProperty("first")
                .Deserialize<WorkerWorkflowEffects.ToolData>(CerebralJson.Options);
            Check(cancelledChild.Status == WorkflowStatus.Cancelled && cancelledChild.Result is null &&
                cancelledChild.Effect?.State == WorkflowEffectState.Cancelled && cancelledChild.State.GetProperty("index").GetInt32() == 1 &&
                firstEvidence?.Content == "fixture-read:fixture.txt" && firstEvidence.Evidence is { Outcome: ToolExecutionOutcome.Succeeded } &&
                firstEvidence.Evidence.RequestId == firstEffectId && fixture.Tools.Contexts.Skip(before).Single().EffectId == firstEffectId &&
                fixture.Tools.Dispatches == before + 1 && cancelledChild.Error?.Contains("source assignment was cancelled", StringComparison.OrdinalIgnoreCase) == true,
                "a settled first stage retains exact success evidence and source cancellation blocks stage two with an honest Cancelled child");
            Check(!cancellationReceipt.Success && cancellationReceipt.Evidence is { Outcome: ToolExecutionOutcome.Cancelled, State: "Cancelled" } &&
                cancellationReceipt.Evidence.JobId == cancelledId && cancellationReceipt.Evidence.RequestId == cancelledSource.Context.EffectId &&
                (await fixture.Host.Runtime.RunAsync(cancelledId)).Revision == cancelledChild.Revision && fixture.Tools.Dispatches == before + 1,
                "the outer receipt reports the exact cancelled child and later runtime inspection never replays its first stage or starts stage two");
        }
        finally
        {
            fixture.Tools.DispatchRelease.TrySetResult();
            await cancelledRunner.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancelledInspector is not null) await cancelledInspector.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Tools.DispatchEntered = null; fixture.Tools.DispatchRelease = null;
        }

        var unknownOuter = await Calling("unknown-outer-source", runCall);
        fixture.Tools.Unknown = true; before = fixture.Tools.Dispatches;
        var unknownOuterReceipt = await dispatcher.ExecuteAsync(runCall, unknownOuter.Context, default);
        Check(!unknownOuterReceipt.Success && unknownOuterReceipt.Evidence?.Outcome == ToolExecutionOutcome.Unknown &&
            unknownOuterReceipt.Evidence.RequestId == unknownOuter.Context.EffectId &&
            unknownOuterReceipt.Evidence.JobId == WorkflowDataToolExtension.RunId(unknownOuter.Context) && fixture.Tools.Dispatches == before + 1,
            "an uncertain child returns a bounded outer Unknown receipt with exact effect/run coordinates");
        var unknownRecovered = await dispatcher.ReconcileAsync(runCall, unknownOuter.Context, unknownOuterReceipt.Evidence, default);
        Check(unknownRecovered.EffectiveOutcome == ToolExecutionOutcome.Unknown && fixture.Tools.Dispatches == before + 1,
            "inspection of an uncertain child does not start its next stage or repeat its first stage");
        fixture.Tools.Unknown = false;
        Check(fixture.Models.Preparations == 0 && fixture.Tools.Reconciliations == 0,
            "host fixture invokes no model and never uses provider replay as recovery");
        Console.WriteLine($"Workflow data host: {checks} SQLite/host/fake-tool checks passed; no live provider, inference, GPU, or installed state.");
    }

    private static WorkflowDataEdition Edition() => new(WorkflowDataCatalog.Schema, "fixture.read-pair", "1.0.0",
        "Read a bounded fixture path twice without an inference stage.",
        Parse("""{"type":"object","properties":{"path":{"type":"string","minLength":1,"maxLength":32768}},"required":["path"],"additionalProperties":false}"""),
        Parse("""{"type":"object","properties":{"first":{"type":"string"},"last":{"type":"string"}},"required":["first","last"],"additionalProperties":false}"""),
        ["filesystem.read"],
        [new("first", "file.read", Parse("""{"path":{"$input":"/path"}}""")),
         new("last", "file.read", Parse("""{"path":{"$input":"/path"}}"""))],
        Parse("""{"first":{"$stage":{"id":"first","pointer":"/content"}},"last":{"$stage":{"id":"last","pointer":"/content"}}}"""));

    private static JsonElement Data(object? value) => JsonSerializer.SerializeToElement(value, CerebralJson.Options);
    private static JsonElement Parse(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }

    private sealed class Fixture : IDisposable
    {
        public const string Project = "fixture-project";
        public const string ForeignProject = "foreign-project";
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cerebral-workflow-data-host-" + Guid.NewGuid().ToString("N"));
        public string AllowedRoot { get; }
        public StateStore State { get; }
        public Configuration Configuration { get; }
        public EventHub Events { get; } = new();
        public AgentProfile Profile { get; }
        public AgentProfile ForeignProfile { get; }
        public FakeTools Tools { get; } = new();
        public UnusedModels Models { get; } = new();
        public WorkerWorkflowEffects Effects { get; private set; } = null!;
        public WorkerWorkflows Host { get; private set; } = null!;

        public Fixture()
        {
            AllowedRoot = Path.Combine(Root, "project"); Directory.CreateDirectory(AllowedRoot);
            State = new StateStore(Path.Combine(Root, "state")); Configuration = new(State);
            var settings = Configuration.Get(); settings.MaxConcurrentJobs = 1; settings.QueueCapacity = 200; Configuration.Save(settings);
            Profile = new AgentProfile { Id = "data-agent", Name = "Data fixture", Role = "helper", Revision = 1,
                Instructions = "Accepted fixture instructions", ModelId = "fixture-model", ModelWorkerId = "local", WorkerId = "local",
                FollowsDefaultModel = false, FollowsModelBudgets = false, AutomaticCompaction = false,
                MaxSteps = 8, MaxSeconds = 60, AllowedTools = ["file.read"], Capabilities = ["filesystem.read"], AllowedRoots = [AllowedRoot] };
            ForeignProfile = CerebralJson.Read<AgentProfile>(Data(Profile)); ForeignProfile.Id = "foreign-agent";
            SaveProfile(); State.Put("agent", ForeignProfile.Id, ForeignProfile);
            State.Put("model", "fixture-model", new ModelRecord { Id = "fixture-model", Path = Path.Combine(Root, "unused.gguf"),
                AutomaticContextSizing = false, ContextTokens = 4096, MaximumContextTokens = 4096, RecommendedContextTokens = 4096 });
            State.Put("navigation", "sidebar", new NavigationState { Nodes =
                [new() { Id = Project, Kind = "project", Title = "Fixture project", Path = AllowedRoot },
                 new() { Id = ForeignProject, Kind = "project", Title = "Foreign fixture project", Path = AllowedRoot }] });
            Restart();
        }
        public void SaveProfile() => State.Put("agent", Profile.Id, Profile);
        public void Restart()
        {
            Effects = new(State, Configuration, Models, Tools, workflowHost: () => Host);
            Host = new(State, Configuration, Events, [new SourceModule(), Effects],
                [new(SourceModule.IdValue, "1.0.0", true, []),
                 new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])], Tools);
        }
        public async Task<WorkflowRun> SourceAsync(string id, AgentProfile? profile = null, string? projectId = Project, int failureLimit = 1)
        {
            profile ??= Profile;
            var snapshot = await WorkerExecutionSnapshot.CaptureAsync(State, profile, Configuration, Tools,
                [new(SourceModule.IdValue, "1.0.0", true, []), new(WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])],
                default, projectId: projectId);
            return Host.Store.Submit(new(id, id + "-chat", profile.Id, SourceModule.DefinitionId, "1.0.0",
                "Exact accepted fixture request", "Private accepted fixture assignment", WorkflowContract.EmptyObject, snapshot, 8,
                new(EventActorKind.User, "owner")) { ProjectId = projectId, ActiveTimeLimitSeconds = profile.MaxSeconds,
                    PolicyStepLimit = profile.MaxSteps, FailureLimits = new Dictionary<string, int> { [WorkerWorkflowEffects.LaunchFailureBudget] = failureLimit } });
        }
        public SubmitWorkflowRequest Request(string id, WorkflowRun source, WorkflowDataEntry entry) => new(id,
            WorkerWorkflows.WorkflowDataConversationId(id), source.Submission.ProfileId, WorkflowDataModule.DefinitionId,
            WorkflowDataModule.DefinitionVersion, "Run the exact fixture edition", null,
            Data(new WorkflowDataRunInput(source.Id, entry.Edition.Id, entry.Edition.Version, entry.Hash,
                Data(new { path = Path.Combine(AllowedRoot, "fixture.txt") }))), 32);
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Root, recursive: true); }
    }

    private sealed class SourceModule : IWorkflowModule
    {
        public const string IdValue = "fixture.data-source";
        public const string DefinitionId = IdValue + ":source";
        public string Id => IdValue;
        public IReadOnlyList<IWorkflowDefinition> Workflows { get; } = [new Definition()];
        public IReadOnlyList<IWorkflowCommandHandler> Commands => [];
        private sealed class Definition : IWorkflowDefinition
        {
            public WorkflowDefinitionDescriptor Descriptor { get; } = new(DefinitionId, "1.0.0", "Fixture accepted source",
                Parse("""{"type":"object","additionalProperties":false}"""), []);
            public WorkflowStep Advance(WorkflowRun run) => new(WorkflowContract.EmptyObject, new CompleteWorkflow(WorkflowContract.EmptyObject));
        }
    }

    private sealed class UnusedModels : IModelRuntimeAdapter
    {
        public int Preparations { get; private set; }
        public ModelAdapterDescriptor Descriptor { get; } = new("unused-data-fixture", "1.0.0", ["text"], false);
        public Task<IPreparedModelGeneration> PrepareAsync(ModelGenerationRequest request, CancellationToken cancellationToken)
        { Preparations++; throw new InvalidOperationException("A workflow DATA fixture must not request model preparation."); }
    }

    private sealed class FakeTools : IToolDispatcher
    {
        public bool FullAccessActive { get; set; }
        public bool ChangedDescriptor { get; set; }
        public bool ExposeSearch { get; set; }
        public bool Unknown { get; set; }
        public IToolExtension? Extension { get; set; }
        public Action? AfterDispatch { get; set; }
        public TaskCompletionSource? DispatchEntered { get; set; }
        public TaskCompletionSource? DispatchRelease { get; set; }
        public int Dispatches { get; private set; }
        public int Reconciliations { get; private set; }
        public List<ToolExecutionContext> Contexts { get; } = [];
        public ToolExecutionContext? LastAttempt { get; private set; }
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var schema = Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}""");
            var result = new List<ToolDescriptor> { new("file.read", ChangedDescriptor ? "Changed fixture read" : "Fixture read", schema, "filesystem.read") };
            if (ExposeSearch) result.Add(new("file.search", "Later fixture search", schema, "filesystem.search"));
            if (Extension is not null) result.AddRange(Extension.Tools);
            return Task.FromResult<IReadOnlyList<ToolDescriptor>>(result);
        }
        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); LastAttempt = context;
            try { ToolDispatcher.ValidateWorkingDirectory(Path.GetDirectoryName(call.Arguments.GetProperty("path").GetString()!)!, context.Profile); }
            catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException)
            { return new ToolResult(false, "Fixture root admission rejected the read.") { Evidence = new(ToolExecutionOutcome.Rejected, "fixture.tools") }; }
            Dispatches++; Contexts.Add(context); AfterDispatch?.Invoke();
            if (DispatchRelease is { } release) { DispatchEntered?.TrySetResult(); await release.Task.WaitAsync(cancellationToken); }
            return new ToolResult(!Unknown, Unknown ? "Fixture unconfirmed result" : "fixture-read:" + Path.GetFileName(call.Arguments.GetProperty("path").GetString()))
                { Evidence = new(Unknown ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Succeeded,
                    "fixture.tools", context.EffectId, State: Unknown ? "unconfirmed" : "completed") };
        }
        public Task<ToolResult> ReconcileAsync(ToolCall call, ToolExecutionContext context, ToolExecutionEvidence? previousEvidence,
            CancellationToken cancellationToken)
        { Reconciliations++; throw new InvalidOperationException("This fixture has no provider recovery adapter."); }
    }
}
