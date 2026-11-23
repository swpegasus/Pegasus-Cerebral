using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    /// <summary>Actual native route and Work adapter with deterministic transport; no model or network.</summary>
    public async Task<int> CheckExplicitWorkflowsAsync()
    {
        Dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run on the active WPF dispatcher.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var originalWorkspace = workflowWorkspace; var originalProfiles = profiles; var originalLead = leadProfile;
        var originalPrompt = prompt.Text; var checks = 0; var calls = new List<CommandRequest>();
        WorkflowRun? saved = null; TaskCompletionSource<CommandResult>? delayed = null;
        var reject = false; var failProjection = false;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        Task<CommandResult> Dispatch(CommandRequest request, CancellationToken token)
        {
            Check(Dispatcher.CheckAccess() && request.ProfileId is null,
                "Explicit work must invoke the captured owner client on the UI dispatcher.");
            calls.Add(request);
            if (request.Operation == "jobs.workflow.submit")
            {
                if (delayed is not null) return delayed.Task.WaitAsync(token);
                if (reject) return Task.FromResult(CommandResult.Fail("The selected profile lacks required workflow capabilities."));
                var value = request.Arguments;
                var submission = new WorkflowSubmission(value.GetProperty("requestId").GetString()!, value.GetProperty("conversationId").GetString()!,
                    value.GetProperty("profileId").GetString()!, value.GetProperty("definitionId").GetString()!, value.GetProperty("definitionVersion").GetString()!,
                    value.GetProperty("userText").GetString()!, value.GetProperty("assignmentText").GetString(), value.GetProperty("input").Clone(),
                    WorkflowContract.EmptyObject, value.GetProperty("stepLimit").GetInt32());
                saved = new() { Id = submission.RequestId, Submission = submission, Revision = 1,
                    CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch };
                return Task.FromResult(CommandResult.Ok(saved));
            }
            if (request.Operation == "jobs.workflow.get")
                return Task.FromResult(saved is null ? CommandResult.Fail("Unknown fixture run.") : CommandResult.Ok(saved));
            if (request.Operation == "jobs.workflow.events.previous")
            {
                if (failProjection) throw new IOException("Fixture projection failure.");
                return Task.FromResult(CommandResult.Ok(new { events = Array.Empty<CerebralEvent>(), snapshotSequence = 0,
                    nextBeforeSequence = (long?)null }));
            }
            throw new InvalidOperationException("Unexpected explicit workflow operation: " + request.Operation);
        }
        using var isolated = new WorkflowWorkspace(Dispatch, () => profiles, (_, _) => { });
        try
        {
            workflowWorkspace = isolated;
            profiles = [new AgentProfile { Id = "captured-profile", Role = "lead", WorkerId = "local" }];
            leadProfile = profiles[0]; prompt.Text = "KEEP THIS CHAT DRAFT";
            var binding = PluginBinding(); var chatId = CurrentConversationId;
            var input = new DesktopWorkflowSubmission(binding, "plugin-work-one", "Write this chapter.", "PRIVATE BOOK CONTEXT")
            { Target = new("pegasus.books:chapter.write", "1.1.0", CerebralJson.Element(new { bookId = "fixture", chapterId = 7 }), 128) };
            delayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = SubmitExplicitDesktopWorkflow(input, deadline.Token);
            Check(isolated.MutationPending && calls.Count == 1 && prompt.Text == "KEEP THIS CHAT DRAFT" && CurrentConversationId == chatId,
                "Explicit plugin dispatch must stage Work identity synchronously without replacing Chat or its draft.");
            var resetBlocked = false;
            try { isolated.Reset(); } catch (InvalidOperationException) { resetBlocked = true; }
            Check(resetBlocked && isolated.Snapshot?.RunId == input.RequestId,
                "Connection reset cannot discard an in-flight explicit-work receipt.");
            checks += await ConnectedWindowConnectionChecks.VerifyRejectedSwitchAsync(this);
            delayed.SetException(new IOException("Fixture lost acknowledgement."));
            var uncertain = await pending;
            Check(uncertain.State == DesktopOperationState.Uncertain && isolated.Snapshot?.MutationUncertain == true,
                "A lost acknowledgement must leave the exact request unconfirmed in Work.");
            resetBlocked = false;
            try { isolated.Reset(); } catch (InvalidOperationException) { resetBlocked = true; }
            Check(resetBlocked, "An uncertain Work request cannot be reset into another service.");
            checks += await ConnectedWindowConnectionChecks.VerifyRejectedSwitchAsync(this);
            var before = calls.Count;
            var unrelated = await SubmitExplicitDesktopWorkflow(input with { RequestId = "replacement-work" }, deadline.Token);
            Check(unrelated.State == DesktopOperationState.Rejected && calls.Count == before,
                "Unrelated work is rejected while a submission receipt is uncertain.");
            var changed = await SubmitExplicitDesktopWorkflow(input with { Context = "CHANGED PAYLOAD" }, deadline.Token);
            Check(changed.State == DesktopOperationState.Rejected && calls.Count == before,
                "A same-ID changed retry is rejected locally without replacing the retained exact request.");
            profiles = [new AgentProfile { Id = "later-selection", Role = "lead", WorkerId = "local" }]; leadProfile = profiles[0];
            delayed = null;
            var accepted = await SubmitExplicitDesktopWorkflow(input, deadline.Token);
            Check(accepted.State == DesktopOperationState.Accepted && saved!.Submission.ProfileId == "captured-profile" &&
                saved.Submission.ConversationId == input.RequestId && saved.Submission.DefinitionId == input.Target.DefinitionId &&
                saved.Submission.DefinitionVersion == "1.1.0" && saved.Submission.StepLimit == 128,
                "Retry must keep the captured profile, separate conversation, definition version and step budget.");
            Check(saved!.Submission.UserText == input.VisibleText && saved.Submission.AssignmentText == input.Context &&
                saved.Submission.Input.GetProperty("chapterId").GetInt64() == 7 &&
                prompt.Text == "KEEP THIS CHAT DRAFT" && CurrentConversationId == chatId,
                "Private handoff and structured input reach Work exactly without composer pollution.");
            before = calls.Count;
            changed = await SubmitExplicitDesktopWorkflow(input with { VisibleText = "Different task" }, deadline.Token);
            Check(changed.State == DesktopOperationState.Rejected && calls.Count == before,
                "An already accepted matching ID cannot falsely confirm a changed payload.");
            var stale = await SubmitExplicitDesktopWorkflow(input with { Binding = new("stale", "elsewhere") }, deadline.Token);
            Check(stale.State == DesktopOperationState.Rejected && calls.Count == before,
                "Stale plugin binding fails before any service call.");

            failProjection = true;
            var projected = await SubmitExplicitDesktopWorkflow(input with { RequestId = "projection-only-failure" }, deadline.Token);
            Check(projected.State == DesktopOperationState.Accepted && projected.Message!.Contains("refresh Work", StringComparison.Ordinal) &&
                isolated.Snapshot?.RunId == "projection-only-failure" && !isolated.Snapshot.MutationUncertain,
                "Confirmed acceptance survives a later display failure without creating another workflow.");
            failProjection = false; reject = true;
            var denied = await SubmitExplicitDesktopWorkflow(input with { RequestId = "capability-denied" }, deadline.Token);
            Check(denied.State == DesktopOperationState.Rejected && denied.Message!.Contains("capabilities", StringComparison.Ordinal) &&
                calls.All(x => x.Operation != "jobs.submit") && prompt.Text == "KEEP THIS CHAT DRAFT",
                "Service admission failures remain actionable, do not add grants and never fall back to legacy Chat.");

            Console.WriteLine(JsonSerializer.Serialize(new { success = true, explicitWorkflowChecks = checks,
                evidence = "deterministic native WPF route and Work adapter with controlled owner transport",
                serviceCommandsExecuted = false, networkUsed = false, windowShown = false }));
            return checks;
        }
        finally
        {
            deadline.Cancel();
            workflowWorkspace = originalWorkspace; profiles = originalProfiles; leadProfile = originalLead; prompt.Text = originalPrompt;
        }
    }
}
