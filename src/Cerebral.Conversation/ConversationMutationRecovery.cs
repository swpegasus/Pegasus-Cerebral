using System.Text.Json;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private const string ExistingDestinationNoWritePrefix =
        "Destination already exists; no write occurred. Call file.read for this exact destination now,";

    private static bool ExistingDestinationDiagnostic(string text)
    {
        if (text.Length > MaximumProjectionBytes) return false;
        // ToolPolicy serializes plain failure text as a JSON string; fixtures and older
        // retained providers may carry the same diagnostic directly. Decode only that
        // bounded string form, never arbitrary response objects or instructions.
        if (text.StartsWith('"'))
        {
            try { text = JsonSerializer.Deserialize<string>(text, Json) ?? ""; }
            catch (JsonException) { return false; }
        }
        return text.StartsWith(ExistingDestinationNoWritePrefix, StringComparison.Ordinal);
    }

    private static bool RejectedExistingDestinationNeedsRead(HarnessState state)
    {
        if (state.LastRejectedTool is not { Name: "file.write", Diagnostic: { } diagnostic } rejected ||
            !ExistingDestinationDiagnostic(diagnostic) ||
            state.ToolReceipts.LastOrDefault(item => item.EffectId == rejected.EffectId) is not
                { Name: "file.write", State: "Failed", Outcome: "Rejected", Truncated: false } ||
            rejected.Arguments.ValueKind != JsonValueKind.Object ||
            !rejected.Arguments.TryGetProperty("path", out var pathValue) ||
            pathValue.ValueKind != JsonValueKind.String)
            return false;
        if (rejected.Arguments.TryGetProperty("collision", out var collision) &&
            (collision.ValueKind != JsonValueKind.String || collision.GetString() != "fail"))
            return false;

        // This removes a misleading generation choice only. It neither changes the saved
        // contract nor authorizes a replacement; normal provider admission still checks paths
        // and the caller-supplied current preimage. Catalog lookup and compaction are not
        // progress. Ordinary actual tool progress clears the retained rejection as before.
        var path = pathValue.GetString();
        return path is null || !TryCanonicalAbsolutePath(path, out var canonical) ||
            !state.KnownFileRevisions.Any(revision => PathEquals(revision.Path, canonical));
    }
}
