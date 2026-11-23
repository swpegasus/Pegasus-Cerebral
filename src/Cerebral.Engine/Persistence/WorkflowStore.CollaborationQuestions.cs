using Cerebral.Contracts.Workflows;

namespace Cerebral.Engine.Persistence;

public sealed partial class WorkflowStore
{
    /// <summary>Read the exact completed owner form saved by the normal answer route.</summary>
    public WorkflowQuestionSet ReadCollaborationOwnerAnswer(string sourceRunId,
        string sourceQuestionSetId, long sourceQuestionRevision)
    {
        Identifier(sourceRunId, nameof(sourceRunId));
        Identifier(sourceQuestionSetId, nameof(sourceQuestionSetId));
        if (sourceQuestionRevision < 2) throw new ArgumentOutOfRangeException(nameof(sourceQuestionRevision));
        using var connection = journal.Open();
        using var query = EventJournal.Command(connection, null, """
            SELECT request,response FROM workflow_answers
            WHERE run_id=$run AND json_extract(response,'$.questions.id')=$set
                AND json_extract(response,'$.questions.revision')=$revision
            ORDER BY rowid DESC LIMIT 1;
            """);
        query.Parameters.AddWithValue("$run", sourceRunId);
        query.Parameters.AddWithValue("$set", sourceQuestionSetId);
        query.Parameters.AddWithValue("$revision", sourceQuestionRevision);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) throw Conflict("The source owner answer is unavailable.");
        var request = Deserialize<WorkflowAnswerRequest>(reader.GetString(0));
        var saved = Deserialize<WorkflowRun>(reader.GetString(1));
        var questions = saved.Questions;
        if (request.RunId != sourceRunId || request.QuestionSetId != sourceQuestionSetId ||
            request.ExpectedQuestionRevision != sourceQuestionRevision - 1 ||
            saved.Id != sourceRunId || saved.Status != WorkflowStatus.Queued ||
            saved.Signal.Kind != WorkflowSignalKind.Answers ||
            questions is null || questions.Id != sourceQuestionSetId ||
            questions.Revision != sourceQuestionRevision ||
            questions.Fields.Any(field => field.Required &&
                (!questions.Answers.TryGetValue(field.Id, out var value) || string.IsNullOrWhiteSpace(value))) ||
            questions.Answers.Any(pair => !questions.Fields.Any(field =>
                field.Id == pair.Key && WorkflowQuestionAnswers.IsValid(field, pair.Value))))
            throw Conflict("The source question has no complete owner answer at this revision.");
        return Freeze(questions);
    }
}
