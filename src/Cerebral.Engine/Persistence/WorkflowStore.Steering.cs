using System.Text;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Microsoft.Data.Sqlite;

namespace Cerebral.Engine.Persistence;

public sealed partial class WorkflowStore
{
    public WorkflowSteeringReceipt Steer(WorkflowSteeringRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Identifier(request.RequestId, nameof(request.RequestId)); Identifier(request.RunId, nameof(request.RunId));
        ValidateSteeringText(request.Text, true); ValidateSteeringText(request.AssignmentText, false);
        return journal.Transact((connection, transaction) =>
        {
            var run = Required(connection, transaction, request.RunId);
            if (run.PreparedInterventions?.Any(x => x.RequestId == request.RequestId) == true)
                throw Conflict("Steering request ID already belongs to a prepared-effect intervention.");
            var prior = run.Steering.SingleOrDefault(x => x.RequestId == request.RequestId);
            if (prior is not null)
            {
                if (prior.Text != request.Text || prior.AssignmentText != request.AssignmentText)
                    throw Conflict("Steering request ID already belongs to different input.");
                return new WorkflowSteeringReceipt(prior.RequestId, run.Id, prior.Sequence, prior.AcceptedAt);
            }
            if (run.IsTerminal || run.CancelRequested) throw Conflict("This workflow has ended or is stopping. Send a new message instead.");
            var (steered, entry) = AppendSteering(connection, transaction, run, request.RequestId,
                request.Text, request.AssignmentText);
            // Do not wake unanswered questions, reconcile uncertain effects, reset limits, or replace
            // the accepted submission. The claimed scheduler incorporates this at its next policy boundary.
            Save(connection, transaction, steered, "steering.accepted");
            return new WorkflowSteeringReceipt(entry.RequestId, steered.Id, entry.Sequence, entry.AcceptedAt);
        });
    }

    private static (WorkflowRun Run, WorkflowSteeringEntry Entry) AppendSteering(
        SqliteConnection connection, SqliteTransaction transaction, WorkflowRun run,
        string requestId, string text, string? assignmentText)
    {
        ValidateSteeringText(text, true); ValidateSteeringText(assignmentText, false);
        if (run.Steering.Count >= 32)
            throw Conflict("This run has reached its 32 steering-message limit.");
        var entry = new WorkflowSteeringEntry(requestId, run.Steering.Count + 1, text,
            assignmentText, DateTimeOffset.UtcNow);
        var entries = run.Steering.Append(entry).ToArray();
        if (entries.Sum(x => Encoding.UTF8.GetByteCount(x.Text) +
                             Encoding.UTF8.GetByteCount(x.AssignmentText ?? "")) > 64 * 1024)
            throw new ArgumentException("Steering content exceeds the run's 64 KiB bound.");
        run = run with { Steering = entries };
        Append(connection, transaction, run, CerebralEventTypes.Message, new(EventActorKind.User),
            [new TextContentPart(text)], new(ModelInclusion.Required), new(EventSourceKind.User),
            Element(new { steeringRequestId = requestId, sequence = entry.Sequence }));
        if (!string.IsNullOrEmpty(assignmentText))
            Append(connection, transaction, run, "workflow.steering.assignment",
                new(EventActorKind.Service, "cerebral.engine"), [new TextContentPart(assignmentText)],
                new(ModelInclusion.Required, HumanVisibility.Hidden),
                new(EventSourceKind.Service, "cerebral.engine"));
        return (run, entry);
    }

    private static void ValidateSteeringText(string? value, bool required)
    {
        if (value is null && !required) return;
        if (value is null || required && string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
            throw new ArgumentException("Steering requires valid nonempty text.");
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(value) > 8192)
                throw new ArgumentException("Each steering text field is limited to 8 KiB of UTF-8.");
        }
        catch (EncoderFallbackException) { throw new ArgumentException("Steering contains invalid Unicode."); }
    }
}
