using System.Text.Json;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private async Task<DesktopControlResponse> HandleDesktopWorkflowControlAsync(DesktopControlRequest request,
        CancellationToken token)
    {
        var workspace = workflowWorkspace ?? throw new InvalidOperationException("The Work view is unavailable.");
        var runId = RequiredString(request.Arguments, "runId", 100);
        WorkflowConversationSnapshot snapshot;
        string? projectionWarning = null;
        switch (request.Operation)
        {
            case "workflow.open":
                EnsureWorkflowArguments(request.Arguments, "runId");
                RequireSemanticAttachCurrent();
                snapshot = await workspace.OpenRunSnapshotAsync(runId, token);
                RequireSemanticAttachCurrent();
                SelectPage("work");
                break;
            case "workflow.inspect":
                EnsureWorkflowArguments(request.Arguments, "runId");
                snapshot = await workspace.InspectRunAsync(runId, token);
                break;
            case "workflow.resume":
                EnsureWorkflowArguments(request.Arguments, "runId", "expectedRevision");
                snapshot = await workspace.ResumeRunAsync(runId,
                    RequiredLong(request.Arguments, "expectedRevision", 1), token);
                break;
            case "workflow.reconcile":
                EnsureWorkflowArguments(request.Arguments, "runId");
                snapshot = await workspace.ReconcileRunAsync(runId, token);
                break;
            case "workflow.answer":
                EnsureWorkflowArguments(request.Arguments, "runId", "questionId", "questionRevision", "answers");
                var questionId = RequiredString(request.Arguments, "questionId", 128);
                if (!request.Arguments.TryGetProperty("questionRevision", out var revision) || revision.ValueKind != JsonValueKind.Number ||
                    !revision.TryGetInt64(out var expectedRevision) || expectedRevision < 1)
                    throw new ArgumentException("Argument 'questionRevision' must be a positive integer.");
                var beforeAnswerRevision = workspace.Snapshot?.Run?.Revision ?? 0;
                try
                {
                    snapshot = await workspace.AnswerRunAsync(new(request.RequestId, runId, questionId,
                        expectedRevision, ReadAnswers(request.Arguments)), token);
                }
                catch when (workspace.Snapshot is { MutationUncertain: true } uncertain && uncertain.RunId == runId &&
                    uncertain.LastMutationRequestId == request.RequestId)
                {
                    return DesktopControlProtocol.Result(request, "uncertain", DesktopWorkflowSnapshot(uncertain),
                        "The answer outcome is not confirmed. Inspect this exact run before retrying; keep its request ID.");
                }
                catch when (workspace.Snapshot is { MutationUncertain: false, Run: { } confirmed } accepted &&
                    confirmed.Id == runId && confirmed.Revision > beforeAnswerRevision && accepted.LastMutationRequestId == request.RequestId)
                {
                    // The mutation receipt was adopted, then a display query failed. Preserve the
                    // confirmed answer rather than turning a subsequent read failure into rejection.
                    snapshot = workspace.Snapshot!;
                    projectionWarning = "The answer was accepted, but the latest display could not be read. Inspect this run to refresh it.";
                }
                break;
            default:
                throw new ArgumentException("Unknown workflow control operation.");
        }
        if (workspace != workflowWorkspace || snapshot.Run?.Id != runId)
            throw new InvalidOperationException("The Work view changed before this operation completed.");
        return DesktopControlProtocol.Result(request,
            request.Operation is "workflow.answer" or "workflow.resume" or "workflow.reconcile" ? "accepted" : "completed",
            DesktopWorkflowSnapshot(snapshot, projectionWarning));
    }

    private static object DesktopWorkflowSnapshot(WorkflowConversationSnapshot snapshot, string? projectionWarning = null)
    {
        var run = snapshot.Run ?? throw new InvalidOperationException("This workflow has no confirmed snapshot.");
        // Keep exact field/choice strings or omit the whole form explicitly. Clipping a choice or an
        // approval label could change its meaning. Large forms remain available in the native UI.
        var questions = run.Questions;
        var questionsOmitted = questions is not null && JsonSerializer.SerializeToUtf8Bytes(questions, CerebralJson.Options).Length > 24 * 1024;
        var resultOmitted = run.Result is { } result && JsonSerializer.SerializeToUtf8Bytes(result, CerebralJson.Options).Length > 8 * 1024;
        var projection = new
        {
            runId = run.Id, run.ConversationId, run.ProfileId, run.DefinitionId, run.DefinitionVersion,
            run.Revision, status = run.Status.ToString().ToLowerInvariant(),
            effectState = run.EffectState?.ToString().ToLowerInvariant(), snapshot.MutationUncertain,
            snapshot.LastMutationRequestId, run.CancelRequested, run.Steps,
            questionId = questions?.Id, questionRevision = questions?.Revision, questionCount = questions?.Fields.Count ?? 0,
            questions = questionsOmitted ? null : questions, questionsOmitted,
            result = resultOmitted ? null : run.Result, resultOmitted, projectionWarning,
            error = ClipWorkflowText(run.Error), waitingReason = ClipWorkflowText(run.WaitingReason)
        };
        if (JsonSerializer.SerializeToUtf8Bytes(projection, CerebralJson.Options).Length > 48 * 1024)
            throw new System.IO.InvalidDataException("Workflow-control metadata exceeds its 48 KiB response bound.");
        return projection;
    }

    private static string? ClipWorkflowText(string? text)
    {
        const int maximum = 1024;
        if (text is null || text.Length <= maximum) return text;
        var end = char.IsHighSurrogate(text[maximum - 1]) ? maximum - 1 : maximum;
        return text[..end] + "…";
    }

    private static void EnsureWorkflowArguments(JsonElement arguments, params string[] allowed)
    {
        EnsureNoExtraArguments(arguments, allowed);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
            if (!names.Add(property.Name)) throw new ArgumentException("Workflow-control argument names must be unique.");
    }
}
