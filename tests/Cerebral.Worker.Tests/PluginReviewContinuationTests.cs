using System.Text.Json;
using Cerebral.Conversation;
using Cerebral.Contracts;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Resources;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Engine.Persistence;
using Cerebral.Engine.Resources;
using Cerebral.Tools;
using Cerebral.Worker;
using Microsoft.Data.Sqlite;

namespace Cerebral.Worker.Tests;

/// <summary>Real isolated SQLite/engine admission with inert package metadata and a descriptor-only dispatcher.
/// No model inference, executable plugin, native control, installed service, or provider connection is used.</summary>
public static partial class PluginReviewContinuationTests
{
    public static async Task RunAsync(bool verbose = true, bool pathAuthorityOnly = false)
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            checks++;
            if (verbose) Console.WriteLine("PASS: " + name);
        }
        var untouched = CerebralJson.Element(new
        {
            workingDirectory = @"C:\Fixture",
            projectId = "exact-project",
            responseSharePercent = 26,
            requiredSuccessfulTools = new[] { "file.read", PluginControlToolExtension.ToolName }
        });
        var prepared = WorkerWorkflows.PreparePluginTestInput(untouched, out var hostAdded);
        Check(PreservesOriginalInput(prepared, untouched) &&
              RequiredNames(prepared).SequenceEqual(["file.read", PluginControlToolExtension.ToolName, InterfaceToolExtension.StatusToolName]) &&
              hostAdded.SequenceEqual([InterfaceToolExtension.StatusToolName]) &&
              RequiredNames(untouched).SequenceEqual(["file.read", PluginControlToolExtension.ToolName]),
            "test input preserves every original field and identifies only the newly inserted host requirement without mutating the frozen input");
        var repairPrepared = WorkerWorkflows.PreparePluginRepairInput(untouched, out var repairAdded);
        var repairRequirements = new[]
        {
            InterfaceToolExtension.StatusToolName, "process.run", PluginScaffoldToolExtension.VerifyToolName,
            InterfaceToolExtension.PluginToolName
        };
        Check(PreservesOriginalInput(repairPrepared, untouched) &&
              RequiredNames(repairPrepared).SequenceEqual(
                  RequiredNames(untouched).Concat(repairRequirements).Distinct(StringComparer.Ordinal)) &&
              repairAdded.SequenceEqual(repairRequirements) &&
              RequiredNames(untouched).SequenceEqual(["file.read", PluginControlToolExtension.ToolName]),
            "repair input preserves original typed fields and requirements while identifying only bounded host status/build/verify/present obligations");
        foreach (var names in new[]
        {
            new[] { InterfaceToolExtension.StatusToolName, PluginControlToolExtension.ToolName },
            Enumerable.Range(1, 6).Select(index => "fixture.tool" + index).ToArray(),
            new[] { InterfaceToolExtension.StatusToolName, PluginControlToolExtension.ToolName }
                .Concat(Enumerable.Range(1, 6).Select(index => "fixture.tool" + index)).ToArray()
        })
        {
            var original = CerebralJson.Element(new { requiredSuccessfulTools = names });
            var result = WorkerWorkflows.PreparePluginTestInput(original, out var added);
            var expectedAdded = new[] { InterfaceToolExtension.StatusToolName, PluginControlToolExtension.ToolName }
                .Where(name => !names.Contains(name, StringComparer.Ordinal)).ToArray();
            Check(RequiredNames(result).SequenceEqual(names.Concat(
                      new[] { InterfaceToolExtension.StatusToolName, PluginControlToolExtension.ToolName }).Distinct(StringComparer.Ordinal)) &&
                  RequiredNames(result).Length <= 8 && added.SequenceEqual(expectedAdded),
                "native obligations union preserves order and exact host-added provenance at the cap: " + names.Length);
        }
        foreach (var invalid in new[]
        {
            "[]", "{\"requiredSuccessfulTools\":null}", "{\"requiredSuccessfulTools\":[\"file.read\",\"file.read\"]}",
            "{\"requiredSuccessfulTools\":[\"\"]}", "{\"requiredSuccessfulTools\":[\"fixture.*\"]}",
            "{\"requiredSuccessfulTools\":[1]}"
        })
        {
            using var document = JsonDocument.Parse(invalid);
            var rejected = false;
            try { WorkerWorkflows.PreparePluginTestInput(document.RootElement); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "malformed original requirements cannot be silently normalized into new test admission: " + invalid);
        }
        foreach (var count in new[] { 7, 8 })
        {
            using var f = await Fixture.CreateAsync(remoteModel: true, input: CerebralJson.Element(new
            {
                requiredSuccessfulTools = Enumerable.Range(1, count).Select(index => "fixture.tool" + index).ToArray()
            }));
            var presentation = f.Present(f.Origin.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null, Message: not null } blocked &&
                  blocked.Message!.Contains("eight-tool limit", StringComparison.Ordinal) &&
                  f.RemoteOwner.Calls.Count == 0 && f.Tools.ExecuteCount == 0 &&
                  f.Workflows.Store.TryGet(presentation.PluginTests!.RequestId) is null,
                "over-cap native obligation union blocks before any remote preflight, inference or durable admission: " + count);
        }

        using (var f = await Fixture.CreateAsync())
        {
            var missingMetadata = JsonSerializer.SerializeToNode(f.Origin.Submission, CerebralJson.Options)!.AsObject();
            missingMetadata.Remove("hostPluginTestMountIds");
            var historical = missingMetadata.Deserialize<WorkflowSubmission>(CerebralJson.Options)!;
            Check(historical.HostPluginTestMountIds.Count == 0,
                "historical submissions with absent mount metadata deserialize to an empty frozen provenance list");

            var rejected = 0;
            foreach (var invalid in new IReadOnlyList<string>[] {
                ["fixture.one", "fixture.one"], ["bad\nmount"], [new string('x', 130)],
                Enumerable.Range(0, WorkflowContract.MaximumPluginTestMounts + 1).Select(index => "fixture." + index).ToArray()
            })
            {
                try { f.Workflows.Store.Submit(f.Origin.Submission with {
                    RequestId = Fixture.Id(), HostPluginTestMountIds = invalid,
                    Caller = new EventActor(EventActorKind.Service, "cerebral.plugin-tests")
                }); }
                catch (ArgumentException) { rejected++; }
            }
            Check(rejected == 4, "the engine rejects duplicate, control-character, oversize and over-count host mount provenance");
            var externalRejected = false;
            try { f.Workflows.Store.Submit(f.Origin.Submission with {
                RequestId = Fixture.Id(), HostPluginTestMountIds = ["fixture.one"],
                Caller = new EventActor(EventActorKind.User, "owner")
            }); }
            catch (ArgumentException) { externalRejected = true; }
            Check(externalRejected, "ordinary owner admission cannot populate host plugin-test mount provenance");

            var mutable = new[] { "fixture.one", "fixture." + new string('é', 121) };
            var expectedMounts = mutable.ToArray();
            var submission = f.Origin.Submission with {
                RequestId = Fixture.Id(), HostPluginTestMountIds = mutable,
                Caller = new EventActor(EventActorKind.Service, "cerebral.plugin-tests")
            };
            var admitted = f.Workflows.Store.Submit(submission);
            mutable[0] = "caller.changed";
            Check(f.Workflows.Store.Get(admitted.Id).Submission.HostPluginTestMountIds.SequenceEqual(expectedMounts),
                "durable admission detaches approved mounts and retains valid Unicode IDs within the native character bound");
            Check(f.Workflows.Store.Submit(admitted.Submission).Id == admitted.Id,
                "an exact approved-mount admission retries its existing run");
            var changedRejected = false;
            try { f.Workflows.Store.Submit(admitted.Submission with { HostPluginTestMountIds = ["fixture.one"] }); }
            catch (WorkflowConflictException) { changedRejected = true; }
            Check(changedRejected, "replay cannot omit or change the frozen approved mount set");
        }

        using (var f = await Fixture.CreateAsync(remoteModel: true))
        {
            var presentation = f.Present(f.Origin.Id);
            var legacyAdmission = f.Workflows.Store.Submit(f.Origin.Submission with
            {
                RequestId = presentation.PluginTests!.RequestId,
                AssignmentText = "Already admitted pre-upgrade test assignment.",
                Caller = new EventActor(EventActorKind.Service, "cerebral.plugin-tests", "Cerebral plugin tests"),
                ConversationSequence = 0
            });
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            var retained = f.Read(presentation.Id).PluginTests!;
            Check(retained is { Status: "admitted" } && retained.RunId == legacyAdmission.Id &&
                  f.Workflows.Store.Get(legacyAdmission.Id).Submission.Input.GetRawText() ==
                      f.Origin.Submission.Input.GetRawText() &&
                  f.Workflows.Store.Get(legacyAdmission.Id).Submission.HostAddedRequiredSuccessfulTools.Count == 0 &&
                  f.Workflows.Store.Get(legacyAdmission.Id).Submission.HostPluginTestMountIds.Count == 0 &&
                  f.RemoteOwner.Calls.Count == 0 && f.Tools.ExecuteCount == 0,
                "pre-upgrade admission recovery fails closed with empty host provenance rather than retroactively deferring old obligations");
        }

        using (var f = await Fixture.CreateAsync())
        {
            var presentation = f.Present(f.Origin.Id);
            var tests = presentation.PluginTests!;
            Check(tests.RequestId == "plugin-tests-" + presentation.Id && tests.OriginRunId == f.Origin.Id &&
                  tests.Round == 1 && tests.Status == "pending" && tests.RunId is null,
                "successful opted-in native sidebar review durably records one deterministic pending continuation");
            var ownerRetry = f.Presentations.Complete(presentation.Id, presentation.ClaimId!, true,
                message: Fixture.OwnerMessage, plugin: Copy(presentation.Plugin!));
            Check(ownerRetry.PluginTests == tests,
                "identical owner-completion retry preserves the existing continuation marker");

            var current = Copy(f.Profile);
            current.Name = "New name"; current.Revision++;
            current.Instructions = "New broader instructions must not enter old accepted work.";
            current.Capabilities.Add("administration");
            current.AllowedTools.Add("*"); current.AllowedRoots.Add(f.ExtraRoot);
            current.MaxSeconds = 25; current.MaxSteps = 5;
            f.Store.Put("agent", current.Id, current);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            var admitted = f.Read(presentation.Id).PluginTests!;
            var run = f.Workflows.Store.Get(admitted.RunId!);
            Check(admitted.Status == "admitted" && run.Id == tests.RequestId &&
                  run.Status == WorkflowStatus.Queued && run.Submission.ConversationId == f.ConversationId &&
                  run.Submission.ProfileId == f.Profile.Id,
                "post-review admission uses the same native conversation/profile and ordinary bounded workflow queue");
            Check(run.Submission.UserText == PluginContinuationGoal.TestStageText &&
                  PreservesOriginalInput(run.Submission.Input, f.Origin.Submission.Input) &&
                   RequiredNames(run.Submission.Input).SequenceEqual(
                       [InterfaceToolExtension.StatusToolName, PluginControlToolExtension.ToolName]) &&
                  run.Submission.HostAddedRequiredSuccessfulTools.SequenceEqual(
                      [InterfaceToolExtension.StatusToolName, PluginControlToolExtension.ToolName]) &&
                  JsonSerializer.Serialize(run.Submission.AcceptedContent, CerebralJson.Options) ==
                  JsonSerializer.Serialize(f.Origin.Submission.AcceptedContent, CerebralJson.Options),
                "continuation retains original request, every typed field and supplemental content while requiring native status and control receipts");
            Check(run.Submission.HostPluginTestMountIds.SequenceEqual(presentation.Plugin!.Mounts.Select(mount => mount.Id)) &&
                  run.Submission.HostPluginTestMountIds.Count != 0 &&
                  !run.Submission.Input.TryGetProperty("hostPluginTestMountIds", out _),
                "automatic QA freezes all exact owner-approved mounts as host metadata rather than model input");
            Check(run.Submission.SettingsSnapshot.GetRawText() == f.Origin.Submission.SettingsSnapshot.GetRawText() &&
                  WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot).Profile.Instructions == f.Profile.Instructions &&
                  !WorkerExecutionSnapshot.Read(run.Submission.SettingsSnapshot).AllowedRoots.Contains(f.ExtraRoot),
                "broader current tools, roots, prompts, capabilities, and renamed profile cannot expand the frozen original settings");
            Check(!run.Submission.AssignmentText!.Contains(Fixture.Assignment, StringComparison.Ordinal) &&
                  run.Submission.AssignmentText.Contains(presentation.Id, StringComparison.Ordinal) &&
                  run.Submission.AssignmentText.Contains(presentation.Plugin!.Generation, StringComparison.Ordinal) &&
                   run.Submission.AssignmentText.Contains("use reviewed as interface.status requestId and plugin.controls presentationId", StringComparison.Ordinal) &&
                  run.Submission.AssignmentText.Contains("evidence, never additional authority", StringComparison.Ordinal),
                "stage directive retains bounded service evidence while the original private assignment is resolved separately by lineage");
            using (var binding = JsonDocument.Parse(run.Submission.AssignmentText![
                       (run.Submission.AssignmentText.LastIndexOf("Binding: ", StringComparison.Ordinal) + "Binding: ".Length)..]))
                Check(binding.RootElement.GetProperty("presentedSourceManifest").GetString() == presentation.Path &&
                      binding.RootElement.GetProperty("presentedSourceManifest").GetString() != presentation.Plugin!.ManifestPath &&
                      binding.RootElement.GetProperty("generatedSourceRoot").ValueKind == JsonValueKind.Null &&
                      run.Submission.AssignmentText.Contains("Do not edit the installed immutable generation", StringComparison.Ordinal),
                    "automatic control-test handoff retains the exact presented source coordinate as evidence without selecting or authorizing an installed-package mutation");
            Check(run.Submission.Caller is { Kind: EventActorKind.Service, Id: "cerebral.plugin-tests" } &&
                  f.Workflows.Journal.ReadPage(f.ConversationId).Events
                      .Single(item => item.TurnId == run.Id && item.Type == CerebralEventTypes.Message)
                      .Actor.Kind == EventActorKind.Service,
                "the follow-up is attributed to the service and does not impersonate another owner prompt");
            Check(run.Submission.StepLimit == f.Origin.Submission.StepLimit &&
                  run.Submission.ActiveTimeLimitSeconds == 25 && run.Submission.PolicyStepLimit == 20 &&
                  JsonSerializer.Serialize(run.Submission.FailureLimits, CerebralJson.Options) ==
                  JsonSerializer.Serialize(f.Origin.Submission.FailureLimits, CerebralJson.Options),
                "current lower time/action limits bound continuation while original failure limits and request step cap remain frozen");
            var count = f.Workflows.Store.ListRecentPage().Runs.Count;
            var reopened = new InterfaceRequests(new StateStore(f.Store.DirectoryPath), f.Events);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            await reopened.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Workflows.Store.ListRecentPage().Runs.Count == count &&
                  f.Read(presentation.Id).PluginTests!.RunId == run.Id && f.Tools.ExecuteCount == 0 &&
                   f.Workflows.Store.Get(run.Id).Submission.Input.GetRawText() == run.Submission.Input.GetRawText() &&
                    f.Workflows.Store.Get(run.Id).Submission.HostPluginTestMountIds.SequenceEqual(run.Submission.HostPluginTestMountIds),
                "duplicate scans and reopened SQLite marker attach one run without performing tools or model work");
            f.Finish(run.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "finished", RunId: not null, Message: not null } final &&
                  final.Message!.Contains("completion is not a functional pass", StringComparison.Ordinal),
                "finished test-run marker reports completion without inventing a functional plugin pass");
        }

        using (var f = await Fixture.CreateAsync(boundPaths: true))
            Check(f.Present(f.Origin.Id, testsAllowed: false).PluginTests is null,
                "without exact owner control-test consent no path-bearing QA stage is admitted");

        using (var f = await Fixture.CreateAsync(boundPaths: true))
        {
            var presentation = f.Present(f.Origin.Id);
            // Project the exact retained source coordinate for this isolated catalog fixture.
            // The generated package itself is never loaded or executed.
            presentation.GeneratedSourceRoot = f.Project;
            f.Store.Put("interface", presentation.Id, presentation);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            var qa = f.Workflows.Store.Get(presentation.PluginTests!.RequestId);
            // This fixture executes only the catalog command; no model adapter or semantic tool is invoked.
            var handler = new WorkerWorkflowEffects(f.Store, f.Configuration, null!, f.Tools).Commands
                .Single(command => command.Descriptor.Id == WorkerWorkflowEffects.CatalogCommand &&
                                   command.Descriptor.Version == 1);
            var exact = new WorkflowCommandContext(Fixture.Id(), qa.Submission,
                CerebralJson.Element(new { names = new[] { PluginControlToolExtension.ToolName } })) { RunId = qa.Id };
            var catalog = await handler.ExecuteAsync(exact, default);
            Check(catalog.State == WorkflowEffectState.Succeeded,
                "approved QA exact catalog succeeds: " + catalog.Error);
            Check(catalog.State == WorkflowEffectState.Succeeded && catalog.Data is { } data &&
                  data.GetProperty("tools").EnumerateArray().Single().GetProperty("name").GetString() == PluginControlToolExtension.ToolName &&
                  data.GetProperty("tools").EnumerateArray().Single().GetProperty("inputSchema").ValueKind == JsonValueKind.Object &&
                  data.GetProperty("pluginSource").GetProperty("root").GetString() == f.Project &&
                  f.Tools.ExecuteCount == 0,
                "approved QA exact catalog retains the original bound path authority and returns the whole control schema without a native action");
            Check(PluginContinuationGoal.PathAuthoritySource(f.Store, f.Workflows.Store, qa.Submission).RequestId == f.Origin.Id,
                "the retained owner-review receipt selects the original path source for approved QA");
            var forged = qa.Submission with { RequestId = "plugin-tests-" + Fixture.Id() };
            var forgedCatalog = await handler.ExecuteAsync(exact with
            {
                Submission = forged
            }, default);
            Check(forgedCatalog.State == WorkflowEffectState.Failed && f.Tools.ExecuteCount == 0,
                "a service-labelled lookalike QA run cannot borrow the approved source path binding");
            var changedMountRejected = false;
            try { PluginContinuationGoal.PathAuthoritySource(f.Store, f.Workflows.Store,
                qa.Submission with { HostPluginTestMountIds = ["fixture.other"] }); }
            catch (UnauthorizedAccessException) { changedMountRejected = true; }
            Check(changedMountRejected && f.Tools.ExecuteCount == 0,
                "path ancestry rejects a changed mount list before granting the original source");
            bool RejectsChangedSource(WorkflowSubmission changed)
            {
                try { PluginContinuationGoal.PathAuthoritySource(f.Store, f.Workflows.Store, changed); }
                catch (UnauthorizedAccessException) { return true; }
                return false;
            }
            Check(RejectsChangedSource(qa.Submission with { ProfileId = "other-profile" }) &&
                  RejectsChangedSource(qa.Submission with { ProjectId = "other-project" }) &&
                  RejectsChangedSource(qa.Submission with { SettingsSnapshot = CerebralJson.Element(new { widenedPaths = true }) }),
                "stale profile, changed project and widened accepted path snapshot cannot borrow the original source");
            var pathNarrowed = Copy(f.Profile);
            pathNarrowed.AllowedRoots.Clear();
            f.Store.Put("agent", pathNarrowed.Id, pathNarrowed);
            var narrowedPathCatalog = await handler.ExecuteAsync(exact, default);
            Check(narrowedPathCatalog.State == WorkflowEffectState.Failed && f.Tools.ExecuteCount == 0,
                "current profile path narrowing still defeats the original accepted QA path grant");
            f.Store.Put("agent", f.Profile.Id, f.Profile);
            var narrowed = Copy(f.Profile);
            narrowed.AllowedTools.Remove(PluginControlToolExtension.ToolName);
            f.Store.Put("agent", narrowed.Id, narrowed);
            var revoked = await handler.ExecuteAsync(exact, default);
            Check(revoked.State == WorkflowEffectState.Failed && f.Tools.ExecuteCount == 0,
                "approved QA ancestry does not restore a currently revoked control-tool grant");
        }
        if (pathAuthorityOnly)
        {
            Console.WriteLine($"Plugin QA path authority checks passed: {checks}");
            return;
        }

        using (var f = await Fixture.CreateAsync())
        {
            Check(f.Present(f.Origin.Id, testsAllowed: false).PluginTests is null,
                "installation without explicit owner control-test opt-in schedules no automatic work");
            Check(f.Present(Fixture.Id(), mounts: []).PluginTests is null,
                "an approved plugin without a sidebar test mount schedules no control continuation");
            Check(f.Present(Fixture.Id(), source: "legacy").PluginTests is null,
                "legacy interface presentation does not silently switch into a native workflow");
            Check(f.Present(Fixture.Id(), success: false).PluginTests is null,
                "failed owner presentation schedules no test continuation");
            var context = new ToolExecutionContext(f.Profile, f.ConversationId, Fixture.Id(), f.Project);
            var file = WorkflowUiOriginTestRuns.SeedPresentation(f.Store, context, "file", f.SourceManifest);
            var claim = f.Presentations.Claim(file.Id, Fixture.Id());
            Check(claim.Claimed, "ordinary file fixture is admitted before completion: " + claim.Request.Error);
            Check(f.Presentations.Complete(file.Id, claim.Request.ClaimId!, true, message: "Fixture file displayed.").PluginTests is null,
                "successful ordinary file presentation contributes no plugin test work");
        }
        using (var f = await Fixture.CreateAsync(acceptedProfile: Fixture.AllowRepairTools))
        {
            foreach (var stage in new[]
            {
                InterfaceFailureStage.PackageInspection,
                InterfaceFailureStage.PackageInstall,
                InterfaceFailureStage.ViewBinding,
                InterfaceFailureStage.ViewCapture,
                InterfaceFailureStage.AttachmentImport,
                InterfaceFailureStage.InstalledGenerationChanged
            })
            {
                var infrastructureFailure = f.Present(Fixture.Id(), success: false, repairAllowed: true,
                    failureStage: stage);
                Check(infrastructureFailure.Status == "failed" &&
                      infrastructureFailure.FailureStage == stage &&
                      infrastructureFailure.Plugin is null && infrastructureFailure.Resource is null &&
                      infrastructureFailure.PluginTests is null,
                    "typed package/presentation infrastructure failure retains truth without automatic source repair: " + stage);
                var explicitRejected = false;
                try { f.Presentations.SchedulePluginRepair(infrastructureFailure.Id, f.ConversationId, true); }
                catch (InvalidOperationException) { explicitRejected = true; }
                Check(explicitRejected && f.Read(infrastructureFailure.Id).PluginTests is null,
                    "explicit owner source repair rejects a known infrastructure failure stage: " + stage);
            }

            var captureFailure = f.Presentations.List(f.ConversationId)
                .First(item => item.FailureStage == InterfaceFailureStage.ViewCapture);
            var captureCompletedAt = captureFailure.CompletedAt;
            var replay = f.Presentations.Complete(captureFailure.Id, captureFailure.ClaimId!, false,
                error: captureFailure.Error, controlTestsAllowed: true,
                failureStage: InterfaceFailureStage.ViewCapture);
            var changedStageRejected = false;
            try
            {
                f.Presentations.Complete(captureFailure.Id, captureFailure.ClaimId!, false,
                    error: captureFailure.Error, controlTestsAllowed: true,
                    failureStage: InterfaceFailureStage.AttachmentImport);
            }
            catch (InvalidOperationException) { changedStageRejected = true; }
            Check(replay.FailureStage == InterfaceFailureStage.ViewCapture &&
                  replay.CompletedAt == captureCompletedAt && replay.PluginTests is null &&
                  changedStageRejected && f.Read(captureFailure.Id).FailureStage == InterfaceFailureStage.ViewCapture,
                "terminal failure replay requires the exact stage and can neither amend classification nor auto-admit infrastructure repair");

            var historical = f.Present(Fixture.Id(), success: false, repairAllowed: true);
            Check(historical.FailureStage is null && historical.PluginTests is null,
                "an unclassified historical-style failure never auto-admits source repair even with prior consent");
            var explicitlyScheduled = f.Presentations.SchedulePluginRepair(historical.Id, f.ConversationId, true);
            Check(explicitlyScheduled.PluginTests is
                  { Mode: InterfacePluginTestModes.RepairBeforeInstall, Status: "pending" },
                "a null-stage historical failure remains available only through explicit owner repair compatibility");

            var contributionFailure = f.Present(Fixture.Id(), success: false, repairAllowed: true,
                failureStage: InterfaceFailureStage.ContributionValidation);
            Check(contributionFailure.FailureStage == InterfaceFailureStage.ContributionValidation &&
                  contributionFailure.PluginTests is
                  { Mode: InterfacePluginTestModes.RepairBeforeInstall, Status: "pending" },
                "explicit consent auto-admits source repair for a typed contribution-validation failure");
        }

        using (var f = await Fixture.CreateAsync(acceptedProfile: Fixture.AllowRepairTools))
        {
            var failed = f.Present(f.Origin.Id, success: false, repairAllowed: true, failureStage: InterfaceFailureStage.NativeLoad);
            var immutableError = failed.Error;
            var immutableFingerprint = failed.PackageFingerprint;
            Check(failed is { Status: "failed", FailureStage: InterfaceFailureStage.NativeLoad,
                      Plugin: null, Resource: null } &&
                  failed.PluginTests is { Mode: InterfacePluginTestModes.RepairBeforeInstall, Status: "pending",
                      RunId: null, Round: 1 } repair &&
                  repair.RequestId == InterfaceRequests.PluginTestPrefix + failed.Id &&
                  repair.OriginRunId == f.Origin.Id,
                "an exact failed owner review with explicit test/repair consent records one pending repair intent without fabricating installation evidence");
            var repeated = f.Presentations.Complete(failed.Id, failed.ClaimId!, false,
                error: immutableError, controlTestsAllowed: true, failureStage: failed.FailureStage);
            Check(repeated.PluginTests == failed.PluginTests && repeated.Status == "failed" &&
                  repeated.Error == immutableError && repeated.PackageFingerprint == immutableFingerprint &&
                  repeated.ClaimId == failed.ClaimId && repeated.CompletedAt == failed.CompletedAt,
                "a lost-response completion retry is idempotent and preserves the immutable failed review coordinates");

            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            var admitted = f.Read(failed.Id).PluginTests!;
            var run = f.Workflows.Store.Get(admitted.RunId!);
            Check(admitted is { Mode: InterfacePluginTestModes.RepairBeforeInstall, Status: "admitted" } &&
                  run.Id == failed.PluginTests!.RequestId && run.Status == WorkflowStatus.Queued &&
                  run.Submission.UserText == PluginContinuationGoal.RepairStageText &&
                  PreservesOriginalInput(run.Submission.Input, f.Origin.Submission.Input) &&
                  RequiredNames(run.Submission.Input).SequenceEqual(repairRequirements) &&
                  run.Submission.HostAddedRequiredSuccessfulTools.SequenceEqual(repairRequirements) &&
                  run.Submission.HostPluginTestMountIds.Count == 0 &&
                  JsonSerializer.Serialize(run.Submission.AcceptedContent, CerebralJson.Options) ==
                      JsonSerializer.Serialize(f.Origin.Submission.AcceptedContent, CerebralJson.Options),
                "repair admission reuses the exact original request/content and adds only status/build/verify/present completion obligations");
            var firstRepairStep = new ConversationAgentHarness().Advance(new AgentHarnessSnapshot(run));
            Check(firstRepairStep.Next is InvokeWorkflowCommand { CommandId: "cerebral.host:history" },
                "the actual admitted repair submission crosses the closed Conversation host-provenance boundary");
            var bindingOffset = run.Submission.AssignmentText!.LastIndexOf("Binding: ", StringComparison.Ordinal);
            using var bindingDocument = JsonDocument.Parse(run.Submission.AssignmentText[(bindingOffset + "Binding: ".Length)..]);
            var repairBinding = bindingDocument.RootElement;
            Check(run.Submission.Caller is { Kind: EventActorKind.Service, Id: "cerebral.plugin-tests" } &&
                  run.Submission.AssignmentText!.Contains(failed.Id, StringComparison.Ordinal) &&
                  run.Submission.AssignmentText.Contains(immutableError!, StringComparison.Ordinal) &&
                  bindingOffset >= 0 && repairBinding.GetProperty("sourceManifest").GetString() == f.SourceManifest &&
                  repairBinding.GetProperty("generatedSourceRoot").ValueKind == JsonValueKind.Null &&
                   run.Submission.AssignmentText.Contains("do not use the reviewed alias", StringComparison.OrdinalIgnoreCase) &&
                   !run.Submission.AssignmentText.Contains("use reviewed as", StringComparison.OrdinalIgnoreCase) &&
                   run.Submission.AssignmentText.Contains("installed or mounted behavior is unverified", StringComparison.OrdinalIgnoreCase) &&
                   !run.Submission.AssignmentText.Contains("failed before installation", StringComparison.OrdinalIgnoreCase) &&
                   !RequiredNames(run.Submission.Input).Contains(PluginControlToolExtension.ToolName, StringComparer.Ordinal),
                "repair assignment carries bounded exact failure/source evidence without reviewed alias, native controls, capture or install claims");
            Check(f.Read(failed.Id) is { Status: "failed", FailureStage: InterfaceFailureStage.NativeLoad,
                      Plugin: null, Resource: null } retained &&
                  retained.Error == immutableError && retained.PackageFingerprint == immutableFingerprint,
                "repair admission leaves the failed presentation status, error, package fingerprint and absent receipts unchanged");
            f.Finish(run.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(failed.Id).PluginTests is { Status: "finished", Message: not null } finished &&
                  finished.Message!.Contains("separate owner review", StringComparison.Ordinal),
                "repair completion reports only a finished source-repair turn and preserves separate owner review");

            var replacement = f.Present(run.Id);
            Check(replacement.PluginTests is { Round: 2, OriginRunId: var origin, Mode: null } &&
                  origin == f.Origin.Id,
                "a later approved installed replacement shares the original lineage and increments the common three-round bound");
        }

        using (var f = await Fixture.CreateAsync(acceptedProfile: Fixture.AllowRepairTools))
        {
            var failed = f.Present(f.Origin.Id, success: false);
            var scheduled = f.Presentations.SchedulePluginRepair(failed.Id, f.ConversationId, true);
            File.AppendAllText(f.SourceManifest, " ");
            var reopened = new InterfaceRequests(new StateStore(f.Store.DirectoryPath), f.Events);
            var repeated = reopened.SchedulePluginRepair(failed.Id, f.ConversationId, true);
            Check(scheduled.PluginTests is { Mode: InterfacePluginTestModes.RepairBeforeInstall, Status: "pending" } &&
                  repeated.PluginTests == scheduled.PluginTests && repeated.Status == "failed" &&
                  repeated.Error == failed.Error && repeated.ClaimId == failed.ClaimId,
                "historical owner-only repair consent is durable and idempotent without replaying the failed installation claim");
            foreach (var invalid in new[] { "wrong-session", f.ConversationId })
            {
                var rejected = false;
                try { reopened.SchedulePluginRepair(failed.Id, invalid, controlTestsAllowed: invalid != f.ConversationId); }
                catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException) { rejected = true; }
                Check(rejected, "repair scheduling rejects foreign conversation or absent explicit consent: " + invalid);
            }
        }

        using (var f = await Fixture.CreateAsync(acceptedProfile: Fixture.AllowRepairTools))
        {
            var failed = f.Present(f.Origin.Id, success: false);
            File.AppendAllText(f.SourceManifest, " ");
            var rejected = false;
            try { f.Presentations.SchedulePluginRepair(failed.Id, f.ConversationId, true); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or JsonException)
            { rejected = true; }
            Check(rejected && f.Read(failed.Id).PluginTests is null && f.Workflows.Store.ListRecentPage().Runs.Count == 1,
                "historical repair consent rejects changed package bytes before durable admission and never rewrites the failed fingerprint");
        }

        using (var f = await Fixture.CreateAsync(acceptedProfile: Fixture.AllowRepairTools))
        {
            var failed = f.Present(f.Origin.Id, success: false, repairAllowed: true, failureStage: InterfaceFailureStage.NativeLoad);
            File.AppendAllText(f.SourceManifest, " ");
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(failed.Id).PluginTests is { Status: "blocked", RunId: null } &&
                  f.Workflows.Store.TryGet(failed.PluginTests!.RequestId) is null,
                "package drift after consent blocks repair before model preflight or workflow admission");
        }

        using (var f = await Fixture.CreateAsync(acceptedProfile: Fixture.AllowRepairTools))
        {
            var failed = f.Present(f.Origin.Id, success: false, repairAllowed: true, failureStage: InterfaceFailureStage.NativeLoad);
            await f.SubmitOriginAsync("New owner request supersedes failed package repair.");
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(failed.Id).PluginTests is { Status: "blocked", RunId: null } &&
                  f.Workflows.Store.TryGet(failed.PluginTests!.RequestId) is null,
                "a newer owner turn blocks stale failed-review repair without replaying installation");
        }

        using (var f = await Fixture.CreateAsync(acceptedProfile: Fixture.AllowRepairTools))
        {
            var failed = f.Present(f.Origin.Id, success: false, repairAllowed: true, failureStage: InterfaceFailureStage.NativeLoad);
            var current = Copy(f.Profile); current.AllowedTools.Remove(InterfaceToolExtension.PluginToolName);
            current.Revision++; f.Store.Put("agent", current.Id, current);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(failed.Id).PluginTests is { Status: "blocked", RunId: null } &&
                  f.Workflows.Store.TryGet(failed.PluginTests!.RequestId) is null,
                "revoked current presentation grant blocks repair admission without widening frozen authority");
        }

        using (var f = await Fixture.CreateAsync())
        {
            var presentation = f.Present(f.Origin.Id);
            presentation.PluginTests = presentation.PluginTests! with { Mode = "unknown-mode" };
            f.Store.Put("interface", presentation.Id, presentation);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null, Message: not null } unknown &&
                  unknown.Message!.Contains("unsupported", StringComparison.OrdinalIgnoreCase) &&
                  f.Workflows.Store.TryGet(presentation.PluginTests.RequestId) is null,
                "an unknown non-null continuation mode fails closed instead of falling through to installed testing");
        }

        using (var f = await Fixture.CreateAsync(acceptedProfile: Fixture.AllowRepairTools))
        {
            var stoppedOriginal = await f.SubmitOriginAsync("Stopped original request for lineage validation.");
            stoppedOriginal = f.Workflows.Store.Fail(stoppedOriginal.Id, stoppedOriginal.Revision, "Stopped original fixture.");
            var completedSource = await f.SubmitOriginAsync("Later completed source with corrupt retained lineage.");
            completedSource = f.Finish(completedSource.Id);
            var failed = f.Present(completedSource.Id, success: false, repairAllowed: true, failureStage: InterfaceFailureStage.NativeLoad);
            failed.PluginTests = failed.PluginTests! with { OriginRunId = stoppedOriginal.Id };
            f.Store.Put("interface", failed.Id, failed);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(failed.Id).PluginTests is { Status: "blocked", RunId: null, Message: not null } blocked &&
                  blocked.Message!.Contains("original request was stopped or failed", StringComparison.OrdinalIgnoreCase),
                "shared continuation authority rejects a stopped original lineage even when its immediate presenting source completed");
        }

        using (var f = await Fixture.CreateAsync(WorkflowStatus.Queued))
        {
            var presentation = f.Present(f.Origin.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "pending", RunId: null, Message: not null } &&
                  f.Workflows.Store.CountActive() == 1,
                "review while its presenting run is active defers rather than submitting overlapping work");
            f.Finish(f.Origin.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "admitted", RunId: not null },
                "the deferred marker admits exactly once after its original run finishes");
        }

        using (var f = await Fixture.CreateAsync())
        {
            var presentation = f.Present(f.Origin.Id);
            var settings = f.Configuration.Get(); settings.QueueCapacity = 1; f.Store.Put("settings", "host", settings);
            var queued = new AgentJob { Id = Fixture.Id(), ProfileId = f.Profile.Id, SessionId = Fixture.Id(), Status = "queued" };
            f.Store.Put("job", queued.Id, queued);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "pending", RunId: null } &&
                  f.Workflows.Store.ListRecentPage().Runs.Count == 1,
                "a full shared legacy/native queue leaves the test intent pending without a partial admission");
            queued.Status = "cancelled"; f.Store.Put("job", queued.Id, queued);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "admitted", RunId: not null },
                "freeing shared queue capacity resumes the retained test intent");
        }

        using (var f = await Fixture.CreateAsync(WorkflowStatus.Queued))
        {
            f.Workflows.Steer(new WorkflowSteeringRequest(Fixture.Id(), f.Origin.Id,
                "Owner correction: preserve the checkpoint labels I already selected."), actor: null);
            var corrected = f.Finish(f.Origin.Id);
            Check(corrected.Steering.Count == 1,
                "fixture accepts an actual owner steering correction before completing its presenting run");
            var presentation = f.Present(f.Origin.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null } &&
                  f.Workflows.Store.ListRecentPage().Runs.Count == 1 &&
                  f.Workflows.Store.TryGet(presentation.PluginTests!.RequestId) is null,
                "accepted owner steering cannot be silently dropped by automatic post-review continuation");
        }

        using (var f = await Fixture.CreateAsync(WorkflowStatus.Queued))
        {
            Check(!f.Workflows.Store.HasAcceptedAnswers(f.Origin.Id),
                "unanswered fixture origin has no durable accepted owner answers");
            var claimed = f.Workflows.Store.TryClaim(f.Origin.Id)!;
            var waiting = f.Workflows.Store.ApplyStep(claimed.Id, claimed.Revision,
                new WorkflowStep(WorkflowContract.EmptyObject,
                    new AskWorkflowQuestions([new WorkflowQuestion("label", "Which checkpoint label should the plugin retain?")])));
            f.Workflows.Answer(new WorkflowAnswerRequest(Fixture.Id(), waiting.Id, waiting.Questions!.Id,
                waiting.Questions.Revision, new Dictionary<string, string> { ["label"] = "Owner selected café checkpoint" }),
                actor: null);
            var answered = f.Finish(f.Origin.Id);
            Check(answered.Questions is null && f.Workflows.Store.HasAcceptedAnswers(answered.Id),
                "accepted owner question answers remain discoverable after completion clears the mutable question set");
            var presentation = f.Present(answered.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null } &&
                  f.Workflows.Store.ListRecentPage().Runs.Count == 1 &&
                  f.Workflows.Store.TryGet(presentation.PluginTests!.RequestId) is null,
                "accepted owner answers cannot be silently omitted by automatic post-review continuation");
        }

        foreach (var stopped in new[] { WorkflowStatus.Cancelled, WorkflowStatus.Failed })
        {
            using var f = await Fixture.CreateAsync(stopped);
            var presentation = f.Present(f.Origin.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null } &&
                  f.Workflows.Store.ListRecentPage().Runs.Count == 1,
                "stopped or failed presenting run never starts automatic plugin work: " + stopped);
        }
        using (var f = await Fixture.CreateAsync())
        {
            var presentation = f.Present(f.Origin.Id);
            await f.SubmitOriginAsync("New owner request supersedes old test intent.");
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null } &&
                  f.Workflows.Store.ListRecentPage().Runs.Count == 2,
                "a newer owner turn blocks stale post-review work even when the older plugin was approved");
        }
        using (var f = await Fixture.CreateAsync())
        {
            var presentation = f.Present(Fixture.Id());
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null },
                "a presentation with no retained source run cannot invent original request authority");
        }

        foreach (var missing in new[] { InterfaceToolExtension.StatusToolName, PluginControlToolExtension.ToolName })
        {
            using var f = await Fixture.CreateAsync(acceptedProfile: profile => profile.AllowedTools.Remove(missing));
            var presentation = f.Present(f.Origin.Id);
            var now = Copy(f.Profile); now.AllowedTools.Add(missing); f.Store.Put("agent", now.Id, now);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null },
                "new current grant cannot add a tool absent from the accepted original snapshot: " + missing);
        }
        foreach (var missing in new[] { InterfaceToolExtension.StatusToolName, PluginControlToolExtension.ToolName })
        {
            using var f = await Fixture.CreateAsync();
            var presentation = f.Present(f.Origin.Id);
            var now = Copy(f.Profile); now.AllowedTools.Remove(missing); f.Store.Put("agent", now.Id, now);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null },
                "revoking a current exact tool grant prevents post-review admission: " + missing);
        }
        foreach (var missing in new[] { "filesystem.read", "desktop.plugins.manage" })
        {
            using var f = await Fixture.CreateAsync();
            var presentation = f.Present(f.Origin.Id);
            var now = Copy(f.Profile); now.Capabilities.Remove(missing); f.Store.Put("agent", now.Id, now);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null },
                "revoking current capability prevents post-review admission: " + missing);
        }
        foreach (var change in new[] { "compactor", "remote-worker", "different-model", "remote-model-route" })
        {
            using var f = await Fixture.CreateAsync();
            var presentation = f.Present(f.Origin.Id);
            var now = Copy(f.Profile);
            switch (change)
            {
                case "compactor": now.Role = "compactor"; break;
                case "remote-worker": now.WorkerId = "remote"; break;
                case "different-model": now.ModelId = "new-model"; break;
                case "remote-model-route": now.ModelWorkerId = "remote"; break;
            }
            f.Store.Put("agent", now.Id, now);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null },
                "current local lead/model identity must match the original snapshot: " + change);
        }
        using (var f = await Fixture.CreateAsync())
        {
            var presentation = f.Present(f.Origin.Id);
            var model = f.Store.Get<ModelRecord>("model", f.Profile.ModelId)!;
            model.ParallelRequests++; f.Store.Put("model", model.Id, model);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default, f.RemoteModels);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null },
                "changed model binding cannot be imported into automatic continuation");
            Check(f.RemoteOwner.Calls.Count == 0,
                "local continuation admission never contacts a configured remote-model verifier");
        }
        using (var f = await Fixture.CreateAsync(remoteModel: true))
        {
            var presentation = f.Present(f.Origin.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default, f.RemoteModels);
            var admitted = f.Read(presentation.Id).PluginTests!;
            var snapshot = WorkerExecutionSnapshot.Read(f.Workflows.Store.Get(admitted.RunId!).Submission.SettingsSnapshot);
            Check(admitted is { Status: "admitted", RunId: not null } &&
                  f.RemoteOwner.Calls.SequenceEqual(["health", "worker.management.status", "models.inspect"]) &&
                  snapshot.Profile.WorkerId == "local" && snapshot.Profile.ModelWorkerId == Fixture.RemoteWorkerId &&
                  snapshot.Model?.ModelWorkerInstallationId == Fixture.RemoteInstallationId,
                "matching remote model and installation admit the exact frozen continuation without local fallback");
        }
        using (var f = await Fixture.CreateAsync(WorkflowStatus.Queued, remoteModel: true))
        {
            var presentation = f.Present(f.Origin.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default, f.RemoteModels);
            Check(f.Read(presentation.Id).PluginTests is { Status: "pending", RunId: null } &&
                  f.RemoteOwner.Calls.Count == 0,
                "an unfinished presenting run remains pending without contacting its remote model worker");
        }
        using (var f = await Fixture.CreateAsync(remoteModel: true))
        {
            var presentation = f.Present(f.Origin.Id);
            var current = Copy(f.Profile); current.AllowedTools.Remove(PluginControlToolExtension.ToolName);
            f.Store.Put("agent", current.Id, current);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default, f.RemoteModels);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null } &&
                  f.RemoteOwner.Calls.Count == 0,
                "a revoked control grant blocks locally before any remote model-worker call");
        }
        using (var f = await Fixture.CreateAsync(remoteModel: true))
        {
            var presentation = f.Present(f.Origin.Id);
            f.RemoteOwner.InstallationId = "replacement-worker-installation";
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default, f.RemoteModels);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null } &&
                  f.Workflows.Store.TryGet(presentation.PluginTests!.RequestId) is null,
                "verified remote installation rotation blocks automatic continuation without creating a run");
        }
        using (var f = await Fixture.CreateAsync(remoteModel: true))
        {
            var presentation = f.Present(f.Origin.Id);
            f.RemoteOwner.Model.ParallelRequests++;
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default, f.RemoteModels);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null } &&
                  f.Workflows.Store.TryGet(presentation.PluginTests!.RequestId) is null,
                "verified remote model-definition drift blocks automatic continuation without consulting the local same-ID model");
        }
        using (var f = await Fixture.CreateAsync(remoteModel: true))
        {
            var presentation = f.Present(f.Origin.Id);
            f.RemoteOwner.Failure = new HttpRequestException("Fixture remote worker unavailable.");
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default, f.RemoteModels);
            Check(f.Read(presentation.Id).PluginTests is { Status: "pending", RunId: null, Message: not null } pending &&
                  pending.Message!.Contains("remains pending", StringComparison.Ordinal) &&
                  f.Workflows.Store.TryGet(presentation.PluginTests!.RequestId) is null,
                "unconfirmed remote availability defers with actionable status instead of fabricating model drift");
            f.RemoteOwner.Failure = null;
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default, f.RemoteModels);
            Check(f.Read(presentation.Id).PluginTests is { Status: "admitted", RunId: not null } &&
                  f.Workflows.Store.ListRecentPage().Runs.Count == 2,
                "a later confirmed remote preflight admits the retained continuation exactly once");
        }
        using (var f = await Fixture.CreateAsync(remoteModel: true))
        {
            var presentation = f.Present(f.Origin.Id);
            f.RemoteOwner.WaitForCancellation = true;
            using var cancelled = new CancellationTokenSource();
            var processing = f.Presentations.ProcessPluginTestsAsync(f.Workflows, cancelled.Token, f.RemoteModels);
            await f.RemoteOwner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancelled.Cancel();
            var propagated = false;
            try { await processing; } catch (OperationCanceledException) { propagated = true; }
            Check(propagated && f.Read(presentation.Id).PluginTests is { Status: "pending", RunId: null } &&
                  f.Workflows.Store.TryGet(presentation.PluginTests!.RequestId) is null,
                "caller cancellation interrupts remote preflight and preserves the unadmitted pending intent");
        }
        using (var f = await Fixture.CreateAsync(remoteModel: true))
        {
            var presentation = f.Present(f.Origin.Id);
            f.RemoteOwner.WaitForRelease = true;
            var processing = f.Presentations.ProcessPluginTestsAsync(f.Workflows, default, f.RemoteModels);
            await f.RemoteOwner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var current = Copy(f.Profile); current.AllowedTools.Remove(PluginControlToolExtension.ToolName);
            f.Store.Put("agent", current.Id, current);
            f.RemoteOwner.Release.TrySetResult(true);
            await processing;
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null } &&
                  f.Workflows.Store.TryGet(presentation.PluginTests!.RequestId) is null,
                "grant revocation during remote preflight is rechecked before durable continuation admission");
        }
        using (var f = await Fixture.CreateAsync())
        {
            var presentation = f.Present(f.Origin.Id);
            var settings = f.Configuration.Get(); settings.SharedDirectory = f.ExtraRoot; f.Store.Put("settings", "host", settings);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null },
                "execution routing drift requires an explicit new request");
        }
        using (var f = await Fixture.CreateAsync())
        {
            var presentation = f.Present(f.Origin.Id);
            f.Store.Put("session", f.ConversationId, new TaskSession { Id = f.ConversationId, ProfileId = f.Profile.Id });
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null },
                "a conflicting legacy conversation route cannot inherit native test continuation");
        }

        using (var f = await Fixture.CreateAsync())
        {
            var presentation = f.Present(f.Origin.Id);
            var originalMarker = presentation.PluginTests!;
            var admittedBeforeCrash = await f.Workflows.SubmitPluginTestsAsync(presentation, default);
            Check(f.Read(presentation.Id).PluginTests!.Status == "pending" && admittedBeforeCrash.Status == WorkflowStatus.Queued,
                "fixture reproduces engine admission committed before marker update");
            var now = Copy(f.Profile);
            now.Capabilities.Clear(); now.AllowedTools.Clear(); now.ModelId = "changed-after-admission";
            f.Store.Put("agent", now.Id, now);
            var settings = f.Configuration.Get(); settings.SharedDirectory = f.ExtraRoot; settings.QueueCapacity = 1;
            f.Store.Put("settings", "host", settings);
            var reopened = new InterfaceRequests(new StateStore(f.Store.DirectoryPath), f.Events);
            await reopened.ProcessPluginTestsAsync(f.Workflows, default);
            var attached = f.Read(presentation.Id).PluginTests!;
            Check(attached.Status == "admitted" && attached.RequestId == originalMarker.RequestId &&
                  attached.RunId == admittedBeforeCrash.Id && f.Workflows.Store.ListRecentPage().Runs.Count == 2 &&
                  f.Workflows.Store.Get(attached.RunId!).Submission.SettingsSnapshot.GetRawText() ==
                      admittedBeforeCrash.Submission.SettingsSnapshot.GetRawText() &&
                   f.Workflows.Store.Get(attached.RunId!).Submission.Input.GetRawText() ==
                       admittedBeforeCrash.Submission.Input.GetRawText() &&
                   f.Workflows.Store.Get(attached.RunId!).Submission.HostAddedRequiredSuccessfulTools.SequenceEqual(
                       admittedBeforeCrash.Submission.HostAddedRequiredSuccessfulTools),
                "crash recovery attaches the exact prior admitted run and its host provenance before changed grants/routes or full capacity");
            f.Workflows.Store.Cancel(attached.RunId!);
            await reopened.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "cancelled" } &&
                  f.Workflows.Store.ListRecentPage().Runs.Count == 2,
                "an owner-cancelled admitted test run is retained and never automatically restarted");
        }
        using (var f = await Fixture.CreateAsync())
        {
            var presentation = f.Present(f.Origin.Id);
            var collision = f.Workflows.Store.Submit(f.Origin.Submission with
            {
                RequestId = presentation.PluginTests!.RequestId,
                ConversationSequence = 0, Caller = new EventActor(EventActorKind.User, "owner"),
                UserText = "Different owner work with a colliding ID."
            });
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null } &&
                  f.Workflows.Store.Get(collision.Id).Submission.UserText == "Different owner work with a colliding ID.",
                "a deterministic-ID collision with different accepted input or caller cannot attach as service recovery");
        }
        using (var f = await Fixture.CreateAsync())
        {
            var presentation = f.Present(f.Origin.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            var tests = f.Read(presentation.Id).PluginTests!;
            var run = f.Workflows.Store.Get(tests.RunId!);
            f.Workflows.Store.Fail(run.Id, run.Revision, "Actual fixture workflow failure.");
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "failed", Message: "Actual fixture workflow failure." } &&
                  f.Workflows.Store.ListRecentPage().Runs.Count == 2,
                "admitted workflow failure remains a distinct failure with its original error and no replacement run");
        }

        using (var f = await Fixture.CreateAsync())
        {
            var source = f.Origin;
            for (var round = 1; round <= 3; round++)
            {
                var presentation = f.Present(source.Id);
                Check(presentation.PluginTests is { Status: "pending" } lineage &&
                      lineage.OriginRunId == f.Origin.Id && lineage.Round == round,
                    "approved replacement derives its original root request and bounded repair round: " + round);
                await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
                source = f.Workflows.Store.Get(f.Read(presentation.Id).PluginTests!.RunId!);
                Check(source.Submission.UserText == PluginContinuationGoal.TestStageText &&
                      source.Submission.AssignmentText!.Split("Service follow-up:", StringSplitOptions.None).Length == 2 &&
                      !source.Submission.AssignmentText.Contains(Fixture.Assignment, StringComparison.Ordinal),
                    "each repair round reuses the original request without recursively growing service assignment: " + round);
                source = f.Finish(source.Id);
            }
            var fourth = f.Present(source.Id);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(fourth.PluginTests is { Round: 4, Status: "blocked", RunId: null } &&
                  f.Workflows.Store.ListRecentPage().Runs.Count == 4,
                "fourth owner-approved replacement preserves evidence but cannot create an endless automatic repair loop");
        }
        using (var f = await Fixture.CreateAsync())
        {
            var orphan = f.Present("plugin-tests-" + Fixture.Id());
            Check(orphan.PluginTests is { Status: "blocked", RunId: null } marker &&
                  marker.Message!.Contains("lineage could not be verified", StringComparison.Ordinal),
                "an orphaned service-looking source cannot fabricate repair lineage");
        }

        using (var f = await Fixture.CreateAsync())
        {
            var eligible = Enumerable.Range(0, 20).Select(index =>
            {
                var presentation = f.Present(Fixture.Id());
                if (index % 2 == 1)
                {
                    presentation.PluginTests = presentation.PluginTests! with { Status = "admitted", RunId = Fixture.Id() };
                    f.Store.Put("interface", presentation.Id, presentation);
                }
                return presentation.Id;
            }).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            // Valid JSON with a non-string ID would fail InterfaceRequest deserialization if terminal
            // history were hydrated. The bounded query must filter these irrelevant rows in SQLite.
            f.Store.Put("interface", Fixture.Id(), new { id = 42, pluginTests = new { status = "finished" } });
            f.Store.Put("interface", Fixture.Id(), new { id = 43, pluginTests = new { status = "blocked" } });
            f.Store.Put("interface", Fixture.Id(), new { id = 44, status = "completed" });
            var first = f.Store.ListPluginTestContinuations(null, 16);
            Check(first.Count == 16 && first.Select(item => item.Id).SequenceEqual(eligible.Take(16)),
                "bounded continuation query selects sixteen ordered pending/admitted intents without hydrating terminal history");
            var second = f.Store.ListPluginTestContinuations(first[^1].Id, 16);
            Check(second.Count == 16 &&
                  second.Select(item => item.Id).SequenceEqual(eligible.Skip(16).Concat(eligible.Take(12))) &&
                  first.Concat(second).Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == 20,
                "continuation cursor rotates past the first batch then wraps so later pending/admitted intents are not starved");
            var wrapped = f.Store.ListPluginTestContinuations(eligible[^1], 16);
            Check(wrapped.Select(item => item.Id).SequenceEqual(eligible.Take(16)) &&
                  new StateStore(f.Store.DirectoryPath).ListPluginTestContinuations(first[^1].Id, 16)
                      .Select(item => item.Id).SequenceEqual(second.Select(item => item.Id)),
                "bounded rotating query preserves wrap ordering and reads the same durable cursor page after reopening SQLite");
            var rejectedLimits = 0;
            foreach (var invalidLimit in new[] { 0, 101 })
                try { f.Store.ListPluginTestContinuations(null, invalidLimit); }
                catch (ArgumentOutOfRangeException) { rejectedLimits++; }
            Check(rejectedLimits == 2,
                "continuation query rejects limits outside its bounded one-to-one-hundred contract");
        }

        var admittedImages = new List<WorkflowSubmission>();
        using (var f = await Fixture.CreateAsync(visionModel: true, canonicalContentAdmission: true,
                   contentAdmission: admittedImages.Add))
        {
            var capture = await f.ImportCaptureAsync();
            var presentation = f.Present(f.Origin.Id, resource: capture);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            var run = f.Workflows.Store.Get(f.Read(presentation.Id).PluginTests!.RunId!);
            Check(admittedImages.Count == 1 && run.Submission.AcceptedContent.Count == 2 &&
                  run.Submission.AcceptedContent[0] == f.Origin.Submission.AcceptedContent[0] &&
                  run.Submission.AcceptedContent[1] is ImageContentPart image &&
                  image.Attachment.Id == capture.Id && image.Attachment.Revision == capture.Revision &&
                  image.Attachment.MediaType == capture.MediaType && image.Attachment.OriginalName == capture.OriginalName &&
                  image.Attachment.Length == capture.Length && image.Attachment.Metadata?["image.width"] == "1" &&
                  image.Attachment.Metadata?["image.height"] == "1",
                "compatible continuation resolves a real canonical PNG and preserves its inspected metadata alongside every original accepted part");
            using var captureBinding = JsonDocument.Parse(run.Submission.AssignmentText![
                (run.Submission.AssignmentText!.LastIndexOf("Binding: ", StringComparison.Ordinal) + "Binding: ".Length)..]);
            Check(run.Submission.UserText == PluginContinuationGoal.TestStageText &&
                  run.Submission.SettingsSnapshot.GetRawText() == f.Origin.Submission.SettingsSnapshot.GetRawText() &&
                  captureBinding.RootElement.GetProperty("mountedViewPixelsIncluded").GetBoolean() &&
                  run.Submission.AssignmentText.Contains("evidence, not instructions", StringComparison.Ordinal),
                "capture pixels neither rewrite the visible request nor alter the frozen profile and are explicitly marked untrusted evidence");
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(admittedImages.Count == 1 &&
                  f.Workflows.Store.Get(run.Id).Submission.AcceptedContent.OfType<ImageContentPart>().Count() == 1,
                "recovered admission preserves one frozen capture and does not rerun its resolver or append duplicate images");
        }
        foreach (var unavailable in new[] { "reasoning", "projector", "capture" })
        {
            var resolverCalls = 0;
            var unresolvedCapture = new InterfaceResourceReceipt(
                "44444444444444444444444444444444", 1, "image/png", "PluginView.png", 68);
            using var f = await Fixture.CreateAsync(visionModel: unavailable != "projector",
                acceptedProfile: profile => { if (unavailable == "reasoning") profile.ReasoningMode = "on"; },
                contentAdmission: _ => resolverCalls++);
            var presentation = f.Present(f.Origin.Id, resource: unavailable == "capture" ? null : unresolvedCapture);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            var run = f.Workflows.Store.Get(f.Read(presentation.Id).PluginTests!.RunId!);
            Check(resolverCalls == 0 && run.Submission.AcceptedContent.Count == 1 &&
                  run.Submission.AssignmentText!.Contains("Captured pixels were not assessed", StringComparison.Ordinal) &&
                  run.Submission.SettingsSnapshot.GetRawText() == f.Origin.Submission.SettingsSnapshot.GetRawText(),
                "unavailable image compatibility keeps the frozen original content and reports metadata-only visual evidence: " + unavailable);
        }
        using (var f = await Fixture.CreateAsync(visionModel: true, exposeAttachmentResolver: false,
                   canonicalContentAdmission: true))
        {
            var capture = await f.ImportCaptureAsync();
            var presentation = f.Present(f.Origin.Id, resource: capture);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null } &&
                   f.Workflows.Store.TryGet(presentation.PluginTests!.RequestId) is null,
                "a missing canonical attachment resolver fails closed before a continuation is queued");
        }
        using (var f = await Fixture.CreateAsync(visionModel: true, canonicalContentAdmission: true))
        {
            var missing = new InterfaceResourceReceipt(Fixture.Id(), 1, "image/png", "PluginView.png", Fixture.CapturePngLength);
            var presentation = f.Present(f.Origin.Id, resource: missing);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null } &&
                  f.Workflows.Store.TryGet(presentation.PluginTests!.RequestId) is null,
                "a missing canonical attachment fails closed without queuing the continuation");
        }
        foreach (var mismatch in new[] { "revision", "media", "name", "length" })
        {
            using var f = await Fixture.CreateAsync(visionModel: true, canonicalContentAdmission: true);
            var canonical = mismatch == "media" ? await f.ImportJpegAsPngReceiptAsync() : await f.ImportCaptureAsync();
            var altered = mismatch switch
            {
                "revision" => canonical with { Revision = canonical.Revision + 1 },
                "name" => canonical with { OriginalName = "ChangedPluginView.png" },
                "length" => canonical with { Length = canonical.Length + 1 },
                _ => canonical
            };
            var presentation = f.Present(f.Origin.Id, resource: altered);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null } &&
                  f.Workflows.Store.TryGet(presentation.PluginTests!.RequestId) is null,
                "canonical capture coordinate and metadata mismatches fail closed before queuing: " + mismatch);
        }
        using (var f = await Fixture.CreateAsync(visionModel: true, canonicalContentAdmission: true))
        {
            var foreign = await f.ImportCaptureAsync(Fixture.Id(), Fixture.Id());
            var presentation = f.Present(f.Origin.Id, resource: foreign);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null } &&
                  f.Workflows.Store.TryGet(presentation.PluginTests!.RequestId) is null,
                "a real canonical PNG from another profile and conversation is indistinguishable from missing and cannot enter the continuation");
        }
        using (var f = await Fixture.CreateAsync(visionModel: true,
                   contentAdmission: _ => throw new UnauthorizedAccessException("Fixture capture is outside the admitted reference scope.")))
        {
            var capture = await f.ImportCaptureAsync();
            var presentation = f.Present(f.Origin.Id, resource: capture);
            await f.Presentations.ProcessPluginTestsAsync(f.Workflows, default);
            Check(f.Read(presentation.Id).PluginTests is { Status: "blocked", RunId: null } &&
                  f.Workflows.Store.TryGet(presentation.PluginTests!.RequestId) is null,
                "the existing accepted-content admission hook can still reject a canonical capture before queuing");
        }

        checks += await PluginContinuationGoalTests.RunAsync();
        checks += await RunSchemaPreflightChecksAsync();
        Console.WriteLine($"Plugin review continuation: {checks} SQLite/engine/metadata fixture checks passed; no live LLM inference or native plugin execution.");
    }

    private static string[] RequiredNames(JsonElement input) =>
        input.GetProperty("requiredSuccessfulTools").EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static bool PreservesOriginalInput(JsonElement prepared, JsonElement original) =>
        original.EnumerateObject().Where(field => field.Name != "requiredSuccessfulTools")
            .All(field => prepared.TryGetProperty(field.Name, out var value) && JsonElement.DeepEquals(field.Value, value)) &&
        prepared.EnumerateObject().Count(field => field.Name != "requiredSuccessfulTools") ==
            original.EnumerateObject().Count(field => field.Name != "requiredSuccessfulTools");

    private static T Copy<T>(T value) => CerebralJson.Read<T>(CerebralJson.Element(value));

    internal sealed class Fixture : IDisposable
    {
        private static readonly byte[] CapturePng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/RxQAAAAASUVORK5CYII=");
        private static readonly byte[] CaptureJpeg =
        [
            0xff, 0xd8, 0xff, 0xc0, 0, 11, 8, 0, 1, 0, 1, 1, 1, 0x11, 0,
            0xff, 0xda, 0, 8, 1, 1, 0, 0, 0x3f, 0, 0, 0xff, 0xd9
        ];
        public const string UserText = "Create a simple checkpoint plugin. Keep café and this exact request.\r\nSecond line.";
        public const string Assignment = "Original private plugin assignment: use only the requested capability.";
        public const string OwnerMessage = "Fixture owner approved and reported mounting; inert DLL metadata was never executed.";
        public const string RemoteWorkerId = "fixture-remote-worker";
        public const string RemoteInstallationId = "fixture-remote-installation";
        private const string ControllerInstallationId = "fixture-controller-installation";
        public static int CapturePngLength => CapturePng.Length;
        private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Cerebral.PluginReviewContinuationTests-" + Id()));
        public string Project { get; }
        public string ExtraRoot { get; }
        public string SourceManifest { get; }
        public string ConversationId { get; } = Id();
        public StateStore Store { get; }
        public EventJournal Journal { get; }
        public AttachmentStore Attachments { get; }
        public Configuration Configuration { get; }
        public EventHub Events { get; } = new();
        public AgentProfile Profile { get; }
        public InterfaceRequests Presentations { get; }
        public WorkerWorkflows Workflows { get; }
        public CatalogOnlyTools Tools { get; } = new();
        public FakeRemoteOwner RemoteOwner { get; }
        public RemoteWorkerManagement RemoteModels { get; }
        public InterfacePluginReceipt Plugin { get; }
        public WorkflowRun Origin { get; private set; } = null!;
        private bool bindOriginPaths;
        public static string Id() => Guid.NewGuid().ToString("N");
        public static void AllowRepairTools(AgentProfile profile)
        {
            foreach (var capability in new[] { "filesystem.write", "process.execute", "desktop.plugins.manage" })
                if (!profile.Capabilities.Contains(capability, StringComparer.Ordinal)) profile.Capabilities.Add(capability);
            foreach (var tool in new[]
            {
                InterfaceToolExtension.StatusToolName, PluginScaffoldToolExtension.ProjectsToolName,
                PluginScaffoldToolExtension.ContractToolName, "file.read", "file.edit", "process.run",
                PluginScaffoldToolExtension.VerifyToolName, InterfaceToolExtension.PluginToolName
            })
                if (!profile.AllowedTools.Contains(tool, StringComparer.Ordinal)) profile.AllowedTools.Add(tool);
        }

        private readonly bool remoteModel;

        private Fixture(Action<AgentProfile>? acceptedProfile, bool remoteModel, bool visionModel,
            Action<WorkflowSubmission>? contentAdmission, bool exposeAttachmentResolver,
            bool canonicalContentAdmission, bool exposeTools)
        {
            this.remoteModel = remoteModel;
            Project = Path.Combine(root, "project"); Directory.CreateDirectory(Project);
            ExtraRoot = Path.Combine(root, "extra"); Directory.CreateDirectory(ExtraRoot);
            SourceManifest = Path.Combine(Project, "desktop-plugin.json");
            const string id = "acceptance.review-continuation";
            const string version = "1.0.0";
            var generation = version + "-" + Id();
            var repository = Path.Combine(root, "plugins");
            var installed = Path.Combine(repository, ".packages", id, generation); Directory.CreateDirectory(installed);
            var settingsDirectory = Path.Combine(repository, ".settings"); Directory.CreateDirectory(settingsDirectory);
            var manifest = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, contract = "pegasus.cerebral.desktop", apiVersion = 1,
                id, name = "Review continuation fixture", version, description = "Inert metadata only.",
                assembly = "fixture.dll", entryType = "Acceptance.ReviewContinuationFixture"
            });
            File.WriteAllText(SourceManifest, manifest);
            File.WriteAllText(Path.Combine(Project, "fixture.dll"), "Admission fixture only. Never loaded.");
            File.WriteAllText(Path.Combine(installed, "desktop-plugin.json"), manifest);
            File.WriteAllText(Path.Combine(installed, "fixture.dll"), "Admission fixture only. Never loaded.");
            File.WriteAllText(Path.Combine(settingsDirectory, id + ".active.json"), JsonSerializer.Serialize(new { Generation = generation }));
            Plugin = new InterfacePluginReceipt(id, version, generation, Path.Combine(installed, "desktop-plugin.json"),
                [new InterfacePluginMountReceipt("sidebar", id + ".counter")], ControlTestsAllowed: true);
            Store = new StateStore(Path.Combine(root, "data"));
            Journal = new EventJournal(Path.Combine(Store.DirectoryPath, "engine.db"));
            Attachments = new AttachmentStore(Journal);
            Configuration = new Configuration(Store);
            var settings = Configuration.Get(); settings.QueueCapacity = 32; settings.DefaultModelId = "fixture-model";
            settings.ProjectsDirectory = Project; Store.Put("settings", "host", settings);
            var modelPath = Path.Combine(root, "fixture.gguf"); File.WriteAllBytes(modelPath, "GGUF"u8.ToArray());
            var localModel = new ModelRecord
            {
                Id = "fixture-model", Path = modelPath, AutomaticContextSizing = false,
                ContextTokens = 8192, MaximumContextTokens = 8192, RecommendedContextTokens = 8192,
                ParallelRequests = 1, GpuLayers = 0
            };
            if (visionModel)
            {
                localModel.VisionProjectorPath = Path.Combine(root, "fixture-projector.gguf");
                File.WriteAllBytes(localModel.VisionProjectorPath, "GGUF"u8.ToArray());
            }
            Store.Put("model", "fixture-model", localModel);
            var remoteRecord = Copy(localModel); remoteRecord.Path = "/models/fixture.gguf";
            RemoteOwner = new(ControllerInstallationId, RemoteInstallationId, remoteRecord);
            if (remoteModel)
            {
                settings = Configuration.Get(); settings.InstallationId = ControllerInstallationId;
                settings.Workers = [new WorkerRegistration
                {
                    Id = RemoteWorkerId, Name = "Fixture remote worker", Url = "http://127.0.0.1:2668",
                    AccessKey = "fixture-owner-access-key-1234567890", RequiresConfiguration = false
                }];
                Store.Put("settings", "host", settings);
            }
            RemoteModels = new RemoteWorkerManagement(Configuration, _ => RemoteOwner);
            Profile = new AgentProfile
            {
                Id = "fixture-lead", Name = "Fixture lead", Role = "lead", WorkerId = "local",
                ModelWorkerId = remoteModel ? RemoteWorkerId : "local",
                ModelId = "fixture-model", AutomaticCompaction = false, FollowsModelBudgets = false,
                ContextTokens = 8192, ResponseTokens = 1024, MaxSteps = 100, MaxSeconds = 300,
                Instructions = "Frozen original lead instructions.", AllowedRoots = [Project],
                Capabilities = ["filesystem.read", "process.execute", "desktop.plugins.manage"],
                AllowedTools = ["file.read", "process.run", InterfaceToolExtension.ToolName,
                    InterfaceToolExtension.PluginToolName, InterfaceToolExtension.StatusToolName, PluginControlToolExtension.ToolName]
            };
            acceptedProfile?.Invoke(Profile);
            Store.Put("agent", Profile.Id, Profile);
            var configuredAdmission = contentAdmission;
            if (canonicalContentAdmission)
            {
                var observer = configuredAdmission;
                configuredAdmission = submission =>
                {
                    var access = new AttachmentAccessContext(submission.ProfileId, submission.ConversationId);
                    foreach (var image in submission.AcceptedContent.OfType<ImageContentPart>())
                        Attachments.ValidateReference(access, image.Attachment);
                    observer?.Invoke(submission);
                };
            }
            Workflows = new WorkerWorkflows(Store, Configuration, Events, [new ConversationWorkflowModule()],
                [ConversationWorkflowModule.PackageBinding, new ServicePluginPackageBinding(
                    WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, [])],
                tools: exposeTools ? Tools : null, acceptedContentAdmission: configuredAdmission, journal: Journal,
                attachments: exposeAttachmentResolver ? Attachments : null);
            Presentations = new InterfaceRequests(Store, Events);
        }

        public static async Task<Fixture> CreateAsync(WorkflowStatus originStatus = WorkflowStatus.Completed,
            Action<AgentProfile>? acceptedProfile = null, bool remoteModel = false, bool visionModel = false,
            Action<WorkflowSubmission>? contentAdmission = null, bool exposeAttachmentResolver = true,
            bool canonicalContentAdmission = false, JsonElement? input = null, bool exposeTools = true,
            string? initialUserText = null,
            Func<Fixture, Task<IReadOnlyList<ContentPart>>>? initialAcceptedContent = null,
            bool boundPaths = false)
        {
            var fixture = new Fixture(acceptedProfile, remoteModel, visionModel, contentAdmission,
                exposeAttachmentResolver, canonicalContentAdmission, exposeTools);
            fixture.bindOriginPaths = boundPaths;
            if (boundPaths)
            {
                fixture.Profile.Revision = 1;
                fixture.Store.Put("agent", fixture.Profile.Id, fixture.Profile);
            }
            var content = initialAcceptedContent is null ? null : await initialAcceptedContent(fixture);
            fixture.Origin = await fixture.SubmitOriginAsync(initialUserText ?? UserText, input, content);
            if (originStatus == WorkflowStatus.Completed) fixture.Origin = fixture.Finish(fixture.Origin.Id);
            else if (originStatus == WorkflowStatus.Cancelled) fixture.Origin = fixture.Workflows.Store.Cancel(fixture.Origin.Id);
            else if (originStatus == WorkflowStatus.Failed)
                fixture.Origin = fixture.Workflows.Store.Fail(fixture.Origin.Id, fixture.Origin.Revision, "Original fixture failed.");
            return fixture;
        }
        public async Task<InterfaceResourceReceipt> ImportCaptureAsync(string? profileId = null,
            string? conversationId = null)
        {
            using var source = new MemoryStream(CapturePng, writable: false);
            var reference = await Attachments.ImportAsync(new AttachmentAccessContext(
                profileId ?? Profile.Id, conversationId ?? ConversationId), Id(), "PluginView.png", source);
            return Receipt(reference);
        }
        public async Task<InterfaceResourceReceipt> ImportJpegAsPngReceiptAsync()
        {
            using var source = new MemoryStream(CaptureJpeg, writable: false);
            var reference = await Attachments.ImportAsync(new AttachmentAccessContext(Profile.Id, ConversationId),
                Id(), "PluginView.jpeg", source);
            return new(reference.Id, reference.Revision, "image/png", reference.OriginalName, reference.Length);
        }
        private static InterfaceResourceReceipt Receipt(AttachmentReference reference) =>
            new(reference.Id, reference.Revision, reference.MediaType, reference.OriginalName, reference.Length);
        public async Task<WorkflowRun> SubmitOriginAsync(string text, JsonElement? input = null,
            IReadOnlyList<ContentPart>? acceptedContent = null)
        {
            var bindings = new[] { ConversationWorkflowModule.PackageBinding, new ServicePluginPackageBinding(
                WorkerWorkflowEffects.ModuleId, WorkerWorkflowEffects.ModuleVersion, true, []) };
            JsonElement snapshot;
            if (!remoteModel)
                snapshot = await WorkerExecutionSnapshot.CaptureAsync(Store, Profile, Configuration, Tools,
                    bindings, CancellationToken.None);
            else
            {
                var descriptors = await Tools.ListAsync(Profile, CancellationToken.None);
                var frozenProfile = Copy(Profile);
                var frozenModel = Copy(RemoteOwner.Model);
                snapshot = CerebralJson.Element(new WorkerExecutionSnapshot.Snapshot(WorkerExecutionSnapshot.Version,
                    frozenProfile, "", "", WorkerExecutionSnapshot.Bind(frozenModel, RemoteWorkerId, RemoteInstallationId),
                    descriptors, frozenProfile.Capabilities.ToArray(), frozenProfile.AllowedTools.ToArray(),
                    frozenProfile.AllowedRoots.ToArray(), false,
                    WorkerExecutionSnapshot.RoutingFingerprint(Configuration.Get()), bindings, []));
            }
            var requestId = Id();
            if (bindOriginPaths)
                snapshot = ConversationPathAuthority.Bind(snapshot, new ConversationPathAuthority.Binding(
                    ScopedPathAccessPolicy.Version, requestId, ConversationId, Profile.Id, null, 0,
                    ConversationPathGrants.Normalize([new(Project, ScopedPathAccess.Write)]))
                { Initial = new(requestId, Profile.Id, Profile.Revision, null, Project) });
            return Workflows.Store.Submit(new WorkflowSubmission(requestId, ConversationId, Profile.Id,
                ConversationWorkflowModule.DefinitionId, ConversationWorkflowModule.DefinitionVersion,
                text, Assignment, input ?? CerebralJson.Element(new { workingDirectory = Project, responseSharePercent = 26 }),
                snapshot, StepLimit: 80, Caller: new EventActor(EventActorKind.User, "owner", "You"))
            {
                AcceptedContent = acceptedContent ?? [new TextContentPart("Supplemental exact café context.\nSecond line.")],
                ActiveTimeLimitSeconds = 300, PolicyStepLimit = 128,
                FailureLimits = new Dictionary<string, int> { ["cerebral.host:launch"] = 2 }
            });
        }
        public WorkflowRun Finish(string id)
        {
            var claimed = Workflows.Store.TryClaim(id) ?? throw new Exception("Fixture run could not be claimed.");
            return Workflows.Store.ApplyStep(id, claimed.Revision,
                new WorkflowStep(WorkflowContract.EmptyObject, new CompleteWorkflow(CerebralJson.Element(new { fixture = "finished" }))));
        }
        public InterfaceRequest Present(string jobId, bool testsAllowed = true, bool success = true,
            string source = "workflow", IReadOnlyList<InterfacePluginMountReceipt>? mounts = null,
            InterfaceResourceReceipt? resource = null, bool repairAllowed = false,
            InterfaceFailureStage? failureStage = null)
        {
            InterfaceRequest request;
            if (source == "workflow")
                request = WorkflowUiOriginTestRuns.SeedPresentation(Store,
                    new ToolExecutionContext(Profile, ConversationId, jobId, Project), "plugin", SourceManifest, claimed: true);
            else
            {
                // Legacy and native conversations are distinct authorities. This negative
                // legacy-continuation case must not poison the native owner's later claim.
                var legacyConversation = Id();
                Store.Put("session", legacyConversation, new TaskSession { Id = legacyConversation, ProfileId = Profile.Id });
                request = new InterfaceRequest
                {
                    SessionId = legacyConversation, JobId = jobId, ProfileId = Profile.Id, Source = source,
                    Kind = "plugin", Path = SourceManifest, Status = "claimed", ClaimId = Id()
                };
                Store.Put("interface", request.Id, request);
            }
            if (request.Status == "pending") request = Presentations.Claim(request.Id, Id()).Request;
            return Presentations.Complete(request.Id, request.ClaimId!, success,
                error: success ? null : "Actual fixture owner presentation failed.",
                message: success ? OwnerMessage : null,
                plugin: success ? Plugin with { ControlTestsAllowed = testsAllowed, Mounts = mounts ?? Plugin.Mounts } : null,
                resource: success ? resource : null,
                controlTestsAllowed: !success && repairAllowed,
                failureStage: failureStage);
        }
        public InterfaceRequest Read(string id) => Store.Get<InterfaceRequest>("interface", id)!;
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    internal sealed class FakeRemoteOwner(string controllerInstallationId, string installationId, ModelRecord model)
        : IRemoteWorkerOwnerClient
    {
        public List<string> Calls { get; } = [];
        public string InstallationId { get; set; } = installationId;
        public ModelRecord Model { get; } = model;
        public Exception? Failure { get; set; }
        public bool WaitForCancellation { get; set; }
        public bool WaitForRelease { get; set; }
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<T> GetAsync<T>(string operation, object? arguments = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(operation);
            Entered.TrySetResult(true);
            if (WaitForCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (WaitForRelease)
                await Release.Task.WaitAsync(cancellationToken);
            if (Failure is not null) throw Failure;
            object value = operation switch
            {
                "health" => new HostInfo("Pegasus Cerebral", "fixture", 1, "Remote fixture", "arm64", "Linux",
                    false, "/var/lib/fixture", InstallationId: InstallationId),
                "worker.management.status" => new WorkerManagementStatus(InstallationId, "assigned",
                    controllerInstallationId, "Fixture controller", null),
                "models.inspect" => Copy(Model),
                _ => throw new InvalidOperationException("Unexpected remote owner operation: " + operation)
            };
            return CerebralJson.Read<T>(CerebralJson.Element(value));
        }

        public void Dispose() { }
    }

    internal sealed class CatalogOnlyTools : IToolDispatcher
    {
        public int ExecuteCount;
        public int ListCount;
        public Func<int, AgentProfile, CancellationToken, Task>? BeforeListAsync;
        public Func<ToolDescriptor, ToolDescriptor>? Transform;
        public string? MissingName;
        public string? DuplicateName;
        public async Task<IReadOnlyList<ToolDescriptor>> ListAsync(AgentProfile profile, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = ++ListCount;
            if (BeforeListAsync is { } before) await before(count, profile, cancellationToken);
            ToolDescriptor[] descriptors =
            [
                new("file.read", "Fixture descriptor only.", WorkflowContract.EmptyObject, "filesystem.read"),
                new("file.edit", "Fixture descriptor only.", WorkflowContract.EmptyObject, "filesystem.write"),
                new("process.run", "Fixture descriptor only.", WorkflowContract.EmptyObject, "process.execute"),
                new(PluginScaffoldToolExtension.ProjectsToolName, "Fixture descriptor only.", WorkflowContract.EmptyObject, "filesystem.read"),
                new(PluginScaffoldToolExtension.ContractToolName, "Fixture descriptor only.", WorkflowContract.EmptyObject, "filesystem.read"),
                new(PluginScaffoldToolExtension.VerifyToolName, "Fixture descriptor only.", WorkflowContract.EmptyObject, "filesystem.read"),
                new(InterfaceToolExtension.ToolName, "Fixture descriptor only.", WorkflowContract.EmptyObject, "filesystem.read"),
                new(InterfaceToolExtension.PluginToolName, "Fixture descriptor only.", WorkflowContract.EmptyObject, "filesystem.read"),
                new(InterfaceToolExtension.StatusToolName, "Fixture descriptor only.", WorkflowContract.EmptyObject, "filesystem.read"),
                new(PluginControlToolExtension.ToolName, "Fixture descriptor only.", WorkflowContract.EmptyObject, "desktop.plugins.manage")
            ];
            var result = descriptors.Where(item => item.Name != MissingName).Select(item => Transform?.Invoke(item) ?? item).ToList();
            if (DuplicateName is { } duplicate) result.Add(descriptors.Single(item => item.Name == duplicate));
            return result;
        }
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            throw new InvalidOperationException("Continuation admission fixtures must not execute a tool or infer with a model.");
        }
    }
}
