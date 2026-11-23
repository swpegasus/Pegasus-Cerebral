using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Media;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private sealed record CompletionOwner(CerebralClient? Client, object Connection, int Version, string? Identity);
    private sealed record CompletionContinuityScope(string InstanceId, string InstallationId, string DataDirectory);
    private sealed record CompletionScope(CompletionOwner Owner, string ConversationId);
    private sealed record CompletionCall(CompletionScope Scope, string RunId, MainCompletionStatus.Operation? Mutation, bool Read, long ReadOrdinal = 0)
    {
        internal long Ordinal => Mutation?.Ordinal ?? ReadOrdinal;
    }
    private readonly MainCompletionStatus mainCompletionStatus = new();
    private CompletionOwner? mainCompletionOwner;
    private CompletionCall? mainCompletionAuthority;
    private string? mainCompletionMirror;
    private Brush? mainCompletionMirrorBrush;
    private volatile bool mainCompletionObservationDegraded;

    private CompletionOwner CurrentCompletionOwner()
    {
        var client = SemanticAttachClient(); var connection = ServiceConnection;
        if (mainCompletionOwner is { } owner && ReferenceEquals(owner.Client, client) &&
            ReferenceEquals(owner.Connection, connection) && owner.Version == knowledgeConnectionVersion &&
            owner.Identity == ServiceConnectionIdentity) return owner;
        mainCompletionOwner = new(client, connection, knowledgeConnectionVersion, ServiceConnectionIdentity);
        // A fixed attachment names the same private Worker and durable data root across
        // transport reconnects. A generic endpoint cannot prove that continuity.
        var attachment = ExistingWorkerAttachment;
        var continuity = attachment is null ? null : new CompletionContinuityScope(
            attachment.Endpoint.InstanceId, attachment.InstallationId, attachment.WorkerDataDirectory);
        mainCompletionStatus.Bind(mainCompletionOwner, continuity);
        mainCompletionAuthority = null; mainCompletionObservationDegraded = false;
        if (CompletionMirrorCurrent()) SetStatus("Completion status is updating for this connection.");
        mainCompletionMirror = null; mainCompletionMirrorBrush = null;
        return mainCompletionOwner;
    }

    private bool CompletionOwnerCurrent(CompletionOwner owner) =>
        ReferenceEquals(owner, mainCompletionOwner) && ReferenceEquals(owner.Client, SemanticAttachClient()) &&
        ReferenceEquals(owner.Connection, ServiceConnection) && owner.Version == knowledgeConnectionVersion &&
        owner.Identity == ServiceConnectionIdentity && NativePreferenceConnectionCurrent(owner.Connection, owner.Identity);

    private CompletionScope? CaptureCompletionScope()
    {
        var owner = CurrentCompletionOwner();
        return CompletionOwnerCurrent(owner) && durableConversationId is { } id ? new(owner, id) : null;
    }

    private void ArmMainCompletionSubmission(string requestId)
    {
        if (CaptureCompletionScope() is { } scope)
            mainCompletionStatus.ArmSubmission(scope.Owner, scope.ConversationId, requestId, nativeChat?.Snapshot?.SnapshotSequence ?? 0);
    }

    // This wraps the existing guarded transport. It neither changes arguments nor retries a call.
    private async Task<CommandResult> CommandWithCompletionOutcomeAsync(CommandRequest request, CancellationToken token)
    {
        var call = await CompletionOnUiAsync(() => CaptureCompletionCall(request));
        CommandResult result;
        try { result = await CommandRetainedNativeChatAsync(request, token).ConfigureAwait(false); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            await CompletionOnUiAsync<object>(() =>
            {
                if (call?.Mutation is { } mutation && CompletionOwnerCurrent(call.Scope.Owner))
                {
                    if (RecordCompletionFailure(call, error.Message, true)) RenderCurrentCompletion();
                }
                return null;
            }).ConfigureAwait(false);
            throw;
        }
        await CompletionOnUiAsync<object>(() =>
        {
            if (call is null || !CompletionOwnerCurrent(call.Scope.Owner)) return null;
            if (result.Success)
            {
                // A rejected stale mutation must not replace a newer run's observation authority.
                if (call.Ordinal < (mainCompletionAuthority?.Ordinal ?? 0)) return null;
                var accepted = call.Mutation is { } mutation
                    ? mainCompletionStatus.ReplyReceived(mutation)
                    : call.Read && mainCompletionStatus.ReadCurrent(call.Scope.Owner, call.Scope.ConversationId, call.ReadOrdinal);
                if (!accepted) return null;
                mainCompletionAuthority = call;
                if (call.Read) mainCompletionStatus.ConfirmRecoveryRead(call.Scope.Owner, call.Scope.ConversationId, call.RunId, call.ReadOrdinal);
            }
            else if (call.Mutation is not null &&
                !RecordCompletionFailure(call, result.Error ?? "The service refused this request.", false)) return null;
            RenderCurrentCompletion();
            return null;
        }).ConfigureAwait(false);
        return result;
    }

    private CompletionCall? CaptureCompletionCall(CommandRequest request)
    {
        var scope = CaptureCompletionScope();
        if (scope is null || request.Arguments.ValueKind != JsonValueKind.Object) return null;
        var snapshot = nativeChat?.Snapshot;
        if (request.Operation == "jobs.workflow.get")
        {
            var id = CompletionArgument(request.Arguments, "id");
            return id is null ? null : new(scope, id, null, true, mainCompletionStatus.BeginRead(scope.Owner, scope.ConversationId));
        }
        var kind = request.Operation switch
        {
            "jobs.workflow.submit" => "submit", "jobs.workflow.answer" => "answer",
            "jobs.workflow.resume" => "resume", _ => null
        };
        if (kind is null || snapshot?.ConversationId != scope.ConversationId) return null;
        var requestId = kind == "resume" ? CompletionArgument(request.Arguments, "id")
            : CompletionArgument(request.Arguments, "requestId");
        var runId = kind == "submit" ? requestId : CompletionArgument(request.Arguments, kind == "answer" ? "runId" : "id");
        if (requestId is null || runId is null || kind == "submit" &&
            CompletionArgument(request.Arguments, "conversationId") != scope.ConversationId) return null;
        if (kind != "submit" && snapshot.Run?.Id != runId) return null;
        var revision = snapshot.Run?.Id == runId ? snapshot.Run.Revision : (long?)null;
        var operation = mainCompletionStatus.Begin(scope.Owner, scope.ConversationId, runId, requestId, kind,
            revision, CompletionMessageSequence(snapshot, runId));
        if (operation is null) return null;
        RenderCurrentCompletion();
        return new(scope, runId, operation, false);
    }

    private bool RecordCompletionFailure(CompletionCall call, string message, bool uncertain)
    {
        if (call.Mutation is not { } operation) return false;
        var snapshot = nativeChat?.Snapshot;
        var same = snapshot?.ConversationId == call.Scope.ConversationId && snapshot.Run?.Id == call.RunId;
        if (!mainCompletionStatus.Fail(operation, message, uncertain, same ? snapshot!.Run!.Revision : null,
            same ? CompletionMessageSequence(snapshot!, call.RunId) : null)) return false;
        if (call.Scope.ConversationId == durableConversationId)
            MirrorCompletionPresentation(mainCompletionStatus.Current(call.Scope.Owner, call.Scope.ConversationId));
        return true;
    }

    private void ObserveMainCompletionSnapshot(WorkflowConversationSnapshot snapshot)
    {
        var scope = CaptureCompletionScope();
        if (scope is null || snapshot.ConversationId != scope.ConversationId) return;
        var progress = MainCompletionProgress(snapshot, nativeChat?.MutationPending == true);
        var authority = mainCompletionAuthority;
        // Reply-time read eligibility can expire before the client publishes its snapshot.
        var readCurrent = authority is null || !authority.Read ||
            mainCompletionStatus.ReadCurrent(scope.Owner, scope.ConversationId, authority.ReadOrdinal);
        MainCompletionStatus.Presentation presentation;
        if (!mainCompletionObservationDegraded && snapshot.Run is { } run && authority is not null &&
            ReferenceEquals(authority.Scope.Owner, scope.Owner) && authority.RunId == run.Id &&
            snapshot.RunId == run.Id && run.ConversationId == scope.ConversationId && readCurrent)
        {
            if (authority.Read)
                mainCompletionStatus.ConfirmRecoveryRead(scope.Owner, scope.ConversationId, run.Id, authority.ReadOrdinal);
            presentation = mainCompletionStatus.Observe(scope.Owner, new(scope.ConversationId, run.Id, run.Revision,
                progress, run.Status == WorkflowStatus.Completed, run.Status == WorkflowStatus.Failed,
                snapshot.MutationUncertain, run.EffectState == WorkflowEffectState.Unknown,
                run.CancelRequested || run.Status == WorkflowStatus.Cancelled, run.Error,
                CompletionMessageSequence(snapshot, run.Id)));
        }
        else presentation = mainCompletionStatus.Current(scope.Owner, scope.ConversationId,
            mainCompletionObservationDegraded ? "Completion status unavailable on this connection" : readCurrent ? progress : null);
        ApplyMainCompletionPresentation(presentation);
    }

    private void RenderCurrentCompletion()
    {
        if (!IsNativeChat || nativeChat?.Snapshot is not { } snapshot ||
            snapshot.ConversationId != durableConversationId) return;
        var owner = CurrentCompletionOwner();
        if (!CompletionOwnerCurrent(owner)) return;
        ApplyMainCompletionPresentation(mainCompletionStatus.Current(owner, snapshot.ConversationId,
            mainCompletionObservationDegraded ? "Completion status unavailable on this connection" : null));
    }

    private void ApplyMainCompletionPresentation(MainCompletionStatus.Presentation presentation)
    {
        sessionState.Text = presentation.Caption;
        sessionState.Foreground = presentation.Error is null ? Ui.Muted : Ui.ErrorText;
        sessionState.ToolTip = presentation.Error is null ? presentation.Progress :
            presentation.Progress + "\nPrevious completion error: " + presentation.Error +
            "\nThe notice clears after a later confirmed successful completion in this conversation.";
        AutomationProperties.SetAutomationId(sessionState, "ChatCompletionStatus");
        AutomationProperties.SetName(sessionState, presentation.Caption);
        if (presentation.ClearedError && CompletionMirrorCurrent())
        {
            SetStatus("Completed successfully.");
            mainCompletionMirror = null; mainCompletionMirrorBrush = null;
        }
        else if (presentation.Error is not null && CompletionMirrorCurrent()) MirrorCompletionPresentation(presentation);
    }

    private void ReportNativeCompletionStatus(string message, bool error)
    {
        // Generic form/read warnings lack mutation coordinates; they never create or clear the latch.
        RenderCurrentCompletion();
        var scope = CaptureCompletionScope();
        var presentation = scope is null ? null : mainCompletionStatus.Current(scope.Owner, scope.ConversationId);
        if (presentation?.Error is not null && CompletionMirrorCurrent())
            MirrorCompletionPresentation(presentation);
        else SetStatus(message, error);
    }

    private void PresentNativeSubmissionResult(CompletionScope? scope, DesktopOperationResult result)
    {
        if (scope is null || !CompletionOwnerCurrent(scope.Owner) || scope.ConversationId != durableConversationId) return;
        var snapshot = nativeChat?.Snapshot;
        if (snapshot?.ConversationId == scope.ConversationId) ObserveMainCompletionSnapshot(snapshot);
        var presentation = mainCompletionStatus.Current(scope.Owner, scope.ConversationId,
            result.State == DesktopOperationState.Accepted && snapshot?.Run is null && snapshot?.MutationUncertain != true
                ? "Accepted · waiting for completion" : null);
        ApplyMainCompletionPresentation(presentation);
        if (presentation.Error is not null) MirrorCompletionPresentation(presentation);
        else SetStatus(result.Message ?? result.State.ToString(),
            result.State is DesktopOperationState.Rejected or DesktopOperationState.Failed or DesktopOperationState.Uncertain);
    }

    private bool CompletionMirrorCurrent() => mainCompletionMirror is not null &&
        Status.Text == mainCompletionMirror && ReferenceEquals(Status.Foreground, mainCompletionMirrorBrush);
    private void MirrorCompletionPresentation(MainCompletionStatus.Presentation presentation)
    {
        SetStatus(presentation.Caption, presentation.Error is not null);
        mainCompletionMirror = Status.Text; mainCompletionMirrorBrush = Status.Foreground;
    }

    private async Task<T?> CompletionOnUiAsync<T>(Func<T?> action) where T : class
    {
        try
        {
            return Dispatcher.CheckAccess() ? action() : await Dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // Preserve the transport result, but refuse completion clearance after any display observation failure.
            mainCompletionObservationDegraded = true;
            return null;
        }
    }

    private static string? CompletionArgument(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 and <= 256 } text && !text.Any(char.IsControl) ? text : null;

    private static long? CompletionMessageSequence(WorkflowConversationSnapshot snapshot, string runId)
    {
        var original = snapshot.OriginalMessage;
        if (original is not null && original.ConversationId == snapshot.ConversationId && original.TurnId == runId)
            return original.Sequence;
        // The same bounded page has already passed Client event identity/order validation.
        return snapshot.Events.Where(item => item.ConversationId == snapshot.ConversationId && item.TurnId == runId &&
            item.Type == CerebralEventTypes.Message && item.Actor.Kind == EventActorKind.User &&
            EventProjection.ShowInTranscript(item)).Select(item => (long?)item.Sequence).FirstOrDefault();
    }

    private static string MainCompletionProgress(WorkflowConversationSnapshot snapshot, bool pending)
    {
        if (snapshot.MutationUncertain || snapshot.Run?.EffectState == WorkflowEffectState.Unknown)
            return "Outcome unconfirmed · refresh saved work";
        if (pending) return "Submitting request · waiting for receipt";
        if (snapshot.Run is not { } run) return "Ready for your message";
        if (run.CancelRequested && !run.IsTerminal) return "Cancelling";
        return MainCompletionStatus.RunPhase(run.Status, run.Questions is not null);
    }
}
