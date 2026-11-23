using System.Text;
using System.Text.Json;

namespace Cerebral.Worker;

/// <summary>Internal generic request policy shared by preparation and completed-output validation.
/// Response allowance and content target are separate inputs; neither can expand accepted/live
/// response authority or context capacity.</summary>
internal readonly record struct WorkerCompactorBudget(int ResponseTokens, int CheckpointCharacters, int CheckpointWords)
{
    internal static WorkerCompactorBudget Create(int completeResponseAllowance, int checkpointContentTarget,
        int acceptedResponseTokens, int liveResponseTokens, int contextTokens)
    {
        if (completeResponseAllowance < 1 || checkpointContentTarget < 1 ||
            acceptedResponseTokens < 1 || liveResponseTokens < 1 || contextTokens < 1)
            throw new ArgumentOutOfRangeException(nameof(completeResponseAllowance), "Compactor budgets must be positive.");
        var response = Math.Max(1, Math.Min(completeResponseAllowance,
            Math.Min(contextTokens / 2, Math.Min(acceptedResponseTokens, liveResponseTokens))));
        var content = Math.Min(checkpointContentTarget, response);
        return new(response, Math.Max(128, checked(content * 3)), Math.Max(40, (content - 32) / 2));
    }

    /// <summary>Narrow content guidance without reducing the admitted complete-response allowance.
    /// These scalar/word ceilings are planning hints, never tokenizer evidence.</summary>
    internal WorkerCompactorBudget NarrowContent(int scalarCeiling, int wordCeiling)
    {
        if (scalarCeiling < 1 || wordCeiling < 1)
            throw new ArgumentOutOfRangeException(nameof(scalarCeiling), "Content ceilings must be positive.");
        return this with
        {
            CheckpointCharacters = Math.Min(CheckpointCharacters, scalarCeiling),
            CheckpointWords = Math.Min(CheckpointWords, wordCeiling)
        };
    }

    internal bool TryNarrowForReduction(int scalars, int words, int allowedHistory, int residualHistory,
        int? framingHistoryHint, out WorkerCompactorBudget narrowed)
    {
        narrowed = this;
        if (scalars < 1 || words < 1 || allowedHistory < 1 || residualHistory <= allowedHistory)
            return false;
        // Tokenization is not additive. Reserve the measured probe only as guidance; the complete
        // owner request must still pass its unchanged tokenizer, manifest and reduction gates.
        var framing = Math.Clamp(framingHistoryHint ?? 0, 0, allowedHistory - 1);
        var payloadTarget = allowedHistory - framing;
        var payloadObserved = Math.Max(payloadTarget + 1, residualHistory - framing);
        var scalarCeiling = Math.Max(1, (int)((long)scalars * payloadTarget / payloadObserved));
        var wordCeiling = Math.Max(1, (int)((long)words * payloadTarget / payloadObserved));
        narrowed = NarrowContent(scalarCeiling, wordCeiling);
        return narrowed.CheckpointCharacters < scalars || narrowed.CheckpointWords < words;
    }

    /// <summary>Accept only the entire completed object and its declared content bounds.
    /// A refused candidate stays refused; never clip a string or extract a valid prefix.</summary>
    internal bool TryReadSummary(string text, out string summary)
    {
        summary = "";
        if (CheckpointCharacters < 1 || CheckpointWords < 1 || string.IsNullOrWhiteSpace(text) ||
            Encoding.UTF8.GetByteCount(text) > 65536) return false;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("checkpoint", out var value) || value.ValueKind != JsonValueKind.String)
                return false;
            var candidate = value.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(candidate) || candidate.Contains('\0') ||
                Encoding.UTF8.GetByteCount(candidate) > 65536) return false;
            // JSON Schema maxLength counts Unicode scalars. Whitespace separates the prompt's
            // word budget; the final prepared-request token oracle remains an independent gate.
            var counts = MeasureSummary(candidate);
            if (counts.Scalars > CheckpointCharacters || counts.Words > CheckpointWords) return false;
            summary = candidate;
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return false; }
    }

    // Both parser ceilings and failure diagnostics use the same scalar/whitespace semantics.
    internal static (int Scalars, int Words) MeasureSummary(string summary)
    {
        var scalars = 0;
        var words = 0;
        var inWord = false;
        foreach (var rune in summary.EnumerateRunes())
        {
            scalars++;
            if (Rune.IsWhiteSpace(rune)) inWord = false;
            else if (!inWord) { words++; inWord = true; }
        }
        return (scalars, words);
    }
}
