using System.Text.Json;

namespace Cerebral.Contracts.Workflows;

/// <summary>Choice validation shared by admission, clients and presentation. The existing string answer
/// envelope stores multi-selection as a JSON string array; model-facing values are actual arrays.</summary>
public static class WorkflowQuestionAnswers
{
    public static bool IsValid(WorkflowQuestion question, string answer)
    {
        if (string.IsNullOrWhiteSpace(answer) || answer.Length > 8192 || answer.Contains('\0')) return false;
        if (question.Multiple) return TrySelections(question, answer, out _);
        return question.Choices is not { Count: > 0 } || question.Choices.Contains(answer, StringComparer.Ordinal);
    }

    public static bool TrySelections(WorkflowQuestion question, string answer, out IReadOnlyList<string> selections)
    {
        selections = Array.Empty<string>();
        if (!question.Multiple || question.Choices is not { Count: > 0 and <= 32 } choices ||
            string.IsNullOrWhiteSpace(answer) || answer.Length > 8192 || answer.Contains('\0')) return false;
        try
        {
            using var document = JsonDocument.Parse(answer, new JsonDocumentOptions { MaxDepth = 2 });
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 1 || value.GetArrayLength() > choices.Count) return false;
            var selected = new List<string>();
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text ||
                    !choices.Contains(text, StringComparer.Ordinal) || !unique.Add(text)) return false;
                selected.Add(text);
            }
            selections = selected.AsReadOnly();
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static string EncodeSelections(WorkflowQuestion question, IEnumerable<string> selections)
    {
        if (!question.Multiple || question.Choices is not { Count: > 0 } choices)
            throw new ArgumentException("Multiple selection requires offered choices.");
        var selected = selections.ToArray();
        if (selected.Length == 0) return ""; // Optional blanks are omitted; required blanks remain incomplete.
        var encoded = JsonSerializer.Serialize(selected);
        if (!TrySelections(question, encoded, out _)) throw new ArgumentException("Select distinct offered choices within the answer limit.");
        return JsonSerializer.Serialize(choices.Where(choice => selected.Contains(choice, StringComparer.Ordinal)).ToArray());
    }

    public static Dictionary<string, object> Values(IReadOnlyList<WorkflowQuestion> fields, IReadOnlyDictionary<string, string> answers)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (id, answer) in answers)
        {
            var field = fields.SingleOrDefault(field => field.Id == id);
            if (field is null || !IsValid(field, answer)) throw new ArgumentException("An answer is unknown or outside the offered choices.");
            values.Add(id, field.Multiple && TrySelections(field, answer, out var selected) ? selected : answer);
        }
        return values;
    }
}
