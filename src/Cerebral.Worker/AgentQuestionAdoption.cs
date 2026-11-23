using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Atomically adopts model-selected question cards only for the exact context revision that produced them.</summary>
internal static class AgentQuestionAdoption
{
    public static bool TryAdopt(StateStore store, string sessionId, int expectedRevision, string jobId,
        IReadOnlyList<UserQuestion> questions, string? optionalAssumptionContext, int requiredBatchSize = 0)
    {
        return store.LinearizedSession(sessionId, () => store.Locked(() =>
        {
            var session = store.GetSessionMetadata(sessionId) ?? throw new ArgumentException("Session not found.");
            if (session.Revision != expectedRevision) return false;
            var job = store.Get<AgentJob>("job", jobId) ?? throw new ArgumentException("Job not found.");
            if (job.SessionId != sessionId) throw new InvalidDataException("Question action conversation identity changed.");
            if (job.CancelRequested) throw new OperationCanceledException("Task stopped before question presentation.");
            if (job.Status != "running") throw new InvalidOperationException("The assignment is no longer running.");
            if (questions.Count is < 1 or > AgentPrompt.MaxPendingOptionalQuestions) return false;
            if (requiredBatchSize > 0 && questions.Count != requiredBatchSize) return false;
            if (questions.Any(x => x.JobId != jobId || x.SessionId != sessionId))
                throw new InvalidDataException("Question action contains a mismatched owner.");
            foreach (var question in questions)
            {
                store.Put("question", question.Id, question);
                store.Append(sessionId, "assistant", "Question: " + question.Prompt);
            }
            if (optionalAssumptionContext is not null)
                store.Append(sessionId, "user", optionalAssumptionContext, origin: "service");
            var required = questions.Where(x => x.Required).ToList();
            if (required.Count > 0)
            {
                job.Status = "waiting-human";
                job.WaitingReason = required.Count == 1
                    ? required[0].Prompt
                    : $"{required.Count} required questions are awaiting separate answers.";
                store.Put("job", job.Id, job);
            }
            return true;
        }));
    }
}
