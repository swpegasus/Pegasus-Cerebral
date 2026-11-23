using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts;
using Cerebral.Contracts.Agents;
using Cerebral.Contracts.Context;
using Cerebral.Contracts.Models;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    // Only ReceiveAnswers records this provenance after matching the host's typed pending form.
    // A source's self-reported category, identifier or digest is not evidence of its author.
    private sealed record AnsweredFormProvenance(string RunId, string SourceId,
        string ProjectionSha256, int OwnerDirectionSequence);

    private static string AnswerProjectionHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static void ValidateAnsweredFormProvenance(HarnessState state, string runId)
    {
        if (state.AnsweredForms is not { } entries) return;
        if (entries.Count is < 1 or > ContextValidation.MaximumSourceCount ||
            entries.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.RunId) || entry.RunId.Length > 128 ||
                string.IsNullOrWhiteSpace(entry.SourceId) || entry.SourceId.Length > 256 ||
                entry.ProjectionSha256 is not { Length: 64 } hash ||
                hash.Any(character => !char.IsAsciiHexDigit(character) || char.IsUpper(character)) ||
                entry.OwnerDirectionSequence < 0 || entry.OwnerDirectionSequence > state.SteeringCount) ||
            entries.Select(entry => entry.SourceId).Distinct(StringComparer.Ordinal).Count() != entries.Count)
            throw new ArgumentException("The saved answer provenance does not belong to this run or owner direction.");
    }

    private static HarnessState PruneAnsweredFormProvenance(HarnessState state, string runId)
    {
        if (state.AnsweredForms is not { } entries) return state;
        var intact = state.Contributions.Where(source => IntactAnswerSource(source, out _))
            .ToDictionary(source => source.Id, StringComparer.Ordinal);
        var retained = entries.Where(entry => entry.RunId == runId &&
            intact.TryGetValue(entry.SourceId, out var source) &&
            IntactAnswerSource(source, out var text) &&
            AnswerProjectionHash(text) == entry.ProjectionSha256).ToArray();
        return state with { AnsweredForms = retained.Length == 0 ? null : retained };
    }

    private static HarnessState RememberAnsweredForm(HarnessState next, AgentHarnessSnapshot snapshot, string text)
    {
        var questions = snapshot.Questions ??
            throw new ArgumentException("Answer provenance requires the host's typed answered form.");
        var sourceId = "answers." + questions.Id + "." + questions.Revision;
        var source = next.Contributions.Single(item => item.Id == sourceId);
        if (!IntactAnswerSource(source, out var retained) || retained != text)
            throw new ArgumentException("The accepted answer projection was not retained intact.");

        // Prune before adding the newly verified typed answer, using the same restore-time rule.
        var entries = (PruneAnsweredFormProvenance(next, snapshot.Run.Id).AnsweredForms ?? []).ToList();
        entries.RemoveAll(entry => entry.SourceId == sourceId);
        entries.Add(new(snapshot.Run.Id, sourceId, AnswerProjectionHash(text), next.SteeringCount));
        var remembered = next with { AnsweredForms = entries.ToArray() };
        ValidateAnsweredFormProvenance(remembered, snapshot.Run.Id);
        return remembered;
    }

    private static bool IntactAnswerSource(ContextContribution source, out string text)
    {
        text = "";
        if (source.Owner != ConversationServicePlugin.ModuleId || source.Category != "user-answers" ||
            source.BeforeCurrentInput || !source.Complete || source.SourceRevision != 1 ||
            source.ExactReadHandle is not null ||
            source.Requirement is not (ContextRequirement.Required or ContextRequirement.Reducible) ||
            source.Messages is not { Count: 1 } ||
            source.Messages[0] is not { Role: "user", Content.Count: 1 } message ||
            message.Content[0] is not TextContentPart part || part.Text is null ||
            Encoding.UTF8.GetByteCount(part.Text) > WorkflowContract.MaximumStateBytes)
            return false;
        text = part.Text;
        return true;
    }

    private static WorkflowStep? CorrectAlreadyAnsweredForm(Input input, HarnessState state,
        IReadOnlyList<WorkflowQuestion> proposed, string runId)
    {
        ValidateAnsweredFormProvenance(state, runId);
        if (state.AnsweredForms is not { } entries) return null;
        var proofs = entries.ToDictionary(entry => entry.SourceId, StringComparer.Ordinal);
        foreach (var source in state.Contributions.Reverse())
        {
            if (!proofs.TryGetValue(source.Id, out var proof) || proof.RunId != runId ||
                proof.OwnerDirectionSequence != state.SteeringCount ||
                !IntactAnswerSource(source, out var text) ||
                AnswerProjectionHash(text) != proof.ProjectionSha256)
                continue;
            string questionSet;
            long revision;
            try
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
                var body = document.RootElement;
                ObjectFields(body, ["questionSet", "revision", "fields", "answers"], ["ownerDirectionSequence"]);
                questionSet = RequiredText(body, "questionSet", 128);
                if (!body.GetProperty("revision").TryGetInt64(out revision) || revision < 1 ||
                    source.Id != "answers." + questionSet + "." + revision)
                    continue;
                if (body.TryGetProperty("ownerDirectionSequence", out var direction))
                {
                    if (!direction.TryGetInt32(out var sequence) || sequence != proof.OwnerDirectionSequence)
                        continue;
                }
                else if (state.SteeringCount != 0 || proof.OwnerDirectionSequence != 0)
                    continue; // Unproven legacy sources never acquire a ledger merely by matching text.

                var fields = body.GetProperty("fields").Deserialize<WorkflowQuestion[]>(Json);
                // Compare typed forms, including defaults, full wording and ordered choices/fields.
                if (fields is null || !JsonElement.DeepEquals(Element(fields), Element(proposed)) ||
                    !CompleteValidAnswers(fields, body.GetProperty("answers")))
                    continue;
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException)
            {
                // An unusable optional match must not manufacture an answer or suppress a real question.
                continue;
            }
            // Repair failures and its retained stop budget must propagate through the existing path.
            return Repair(input, state,
                "The owner already answered this exact form in this run. Use the intact user-answers source " +
                source.Id + " (question set " + questionSet + ", revision " + revision +
                ") and continue the accepted task. Do not ask the same form again without new owner direction.",
                "already-answered-form:" + Element(proposed).GetRawText());
        }
        return null;
    }

    private static bool CompleteValidAnswers(IReadOnlyList<WorkflowQuestion> fields, JsonElement answers)
    {
        if (answers.ValueKind != JsonValueKind.Object) return false;
        UniqueProperties(answers);
        foreach (var answer in answers.EnumerateObject())
        {
            var field = fields.SingleOrDefault(item => item.Id == answer.Name);
            if (field is null) return false;
            var value = field.Multiple ? answer.Value.GetRawText() :
                answer.Value.ValueKind == JsonValueKind.String ? answer.Value.GetString() : null;
            if (value is null || !WorkflowQuestionAnswers.IsValid(field, value)) return false;
        }
        return fields.Where(field => field.Required).All(field => answers.TryGetProperty(field.Id, out _));
    }
}
