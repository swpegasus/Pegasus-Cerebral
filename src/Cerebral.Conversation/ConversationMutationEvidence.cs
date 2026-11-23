using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private const int MaximumMutationEvidenceBytes = 2048;

    private static string AppendMutationEvidence(Input input, HarnessState state, WorkflowEffect effect,
        ToolData? data, string content)
    {
        if (state.PendingName is not ("file.edit" or "file.write") ||
            PendingToolArguments(state) is not { } arguments || !SuccessfulEffect(effect, data) ||
            !TryActionPath(input, arguments, "path", out var path))
            return content;
        var disposition = ReadMutationDisposition(input, state.PendingName, arguments, data, successful: true, effect.Id);
        if (disposition == MutationDisposition.KnownNoOp) return AppendNoOpMutationEvidence(state, effect, content);
        if (disposition != MutationDisposition.Actual) return content;

        // Only confirmed mutation evidence may explain why retained read text is historical.
        // The raw request stays in its ledger; generated write bodies never enter this summary.
        using var document = JsonDocument.Parse(data!.Content);
        var sha256 = document.RootElement.GetProperty("sha256").GetString()!;
        var summary = new Dictionary<string, object?>
        {
            ["receipt"] = effect.Id,
            ["tool"] = state.PendingName,
            ["path"] = path,
            ["sha256"] = sha256,
            ["note"] = "Prior reads for this target predate this applied mutation. Read a fresh bounded range for current source when needed."
        };
        if (state.PendingName == "file.edit" &&
            arguments.TryGetProperty("oldText", out var oldValue) && oldValue.ValueKind == JsonValueKind.String &&
            arguments.TryGetProperty("newText", out var newValue) && newValue.ValueKind == JsonValueKind.String)
        {
            var oldText = oldValue.GetString()!;
            var newText = newValue.GetString()!;
            summary["oldCharacters"] = oldText.Length;
            summary["newCharacters"] = newText.Length;
            if ((long)Encoding.UTF8.GetByteCount(oldText) + Encoding.UTF8.GetByteCount(newText) <= MaximumMutationEvidenceBytes)
            {
                summary["delta"] = new { oldText, newText };
                if (!MutationEvidenceFits(summary)) summary.Remove("delta");
            }
            if (!summary.ContainsKey("delta")) summary["deltaOmitted"] = true;
        }
        else summary["contentOmitted"] = true;

        // Keep identity bounded without presenting a chopped path as an exact coordinate.
        // The receipt preserves the full target when even its escaped path exceeds this cap.
        if (!MutationEvidenceFits(summary))
        {
            summary.Remove("path");
            summary["targetPathSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
            summary["pathOmitted"] = true;
            summary["note"] = "Prior reads for this target predate this applied mutation. Fetch this receipt for the exact path, then read a fresh bounded range when needed.";
        }
        if (!MutationEvidenceFits(summary))
        {
            summary.Remove("receipt");
            summary["receiptInHeader"] = true;
        }
        return content + MutationEvidenceText(summary);
    }

    private static string MutationEvidenceText(Dictionary<string, object?> summary) =>
        "\nApplied mutation:\n" + JsonSerializer.Serialize(summary, Json);

    private static bool MutationEvidenceFits(Dictionary<string, object?> summary) =>
        Encoding.UTF8.GetByteCount(MutationEvidenceText(summary)) <= MaximumMutationEvidenceBytes;
}
