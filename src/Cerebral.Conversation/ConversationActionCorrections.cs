using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Models;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private const int MaximumActionCorrections = 8;
    private sealed record ActionCorrection(string IdentitySha256, int Attempts);

    // Specific admission/replay/completion guards keep their own unchanged two-correction bounds.
    // This ledger distinguishes general model failures between complete actions, while keeping an
    // overall backstop against a model alternating diagnostics without producing an action.
    private static string? TryRecordActionCorrection(HarnessState state, string identity, out HarnessState next)
    {
        next = state;
        ValidateActionCorrections(state);
        if (state.ActionCorrections is null && state.Repairs >= MaximumRepairs)
            return "Stopped after the retained two action corrections. No complete action advanced this stage.";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var entries = (state.ActionCorrections ?? []).ToList();
        var prior = entries.FindIndex(entry => entry.IdentitySha256 == hash);
        if (prior >= 0 && entries[prior].Attempts >= MaximumRepairs)
            return "Stopped after two corrections for the same unresolved model failure. Change the approach or split the work before retrying.";
        if (state.Repairs >= MaximumActionCorrections)
            return "Stopped after eight action corrections without a complete action. Distinct diagnostics did not establish progress.";
        // A legacy in-flight window has no failure identity. Conservatively carry its spent attempts
        // into the first identified correction rather than silently replenishing them on upgrade.
        var attempts = prior < 0 ? (state.ActionCorrections is null ? state.Repairs : 0) + 1 : entries[prior].Attempts + 1;
        if (prior < 0) entries.Add(new(hash, attempts)); else entries[prior] = entries[prior] with { Attempts = attempts };
        next = state with { Repairs = checked(state.Repairs + 1), ActionCorrections = entries.ToArray() };
        return null;
    }

    private static void ValidateActionCorrections(HarnessState state)
    {
        if (state.Repairs is < 0 or > MaximumActionCorrections ||
            state.ActionCorrections is { } entries && (entries.Count is < 1 or > MaximumActionCorrections ||
                entries.Any(entry => entry is null || entry.IdentitySha256 is not { Length: 64 } hash ||
                    !hash.All(char.IsAsciiHexDigit) || entry.Attempts is < 1 or > MaximumRepairs) ||
                entries.Select(entry => entry.IdentitySha256).Distinct(StringComparer.Ordinal).Count() != entries.Count ||
                entries.Sum(entry => entry.Attempts) != state.Repairs))
            throw new ArgumentException("The saved action-correction ledger is invalid.");
    }

    private static string GenerationCorrectionIdentity(ModelGenerationResult generation, PreparedModelMetadata? prepared) =>
        JsonSerializer.Serialize(new { kind = "generation", generation.Finish, prepared?.EffectiveContext,
            prepared?.ResponseBudget, prepared?.ReasoningReserveTokens, prepared?.SafetyReserveTokens }, Json);

    private static string ActionFormatCorrectionIdentity(Exception error)
    {
        // Byte offsets change when a model rephrases the same malformed JSON; they are not progress.
        // Field/index details must not create fresh retries for the same string-type failure.
        var message = error is DelegateOptionalTextTypeException ? "A string is required." : error.Message;
        var position = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        if (position >= 0) message = message[..position];
        return "action-format:" + message;
    }
}
