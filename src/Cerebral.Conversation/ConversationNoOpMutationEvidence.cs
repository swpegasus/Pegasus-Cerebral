using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerebral.Contracts.Workflows;

namespace Cerebral.Conversation;

public sealed partial class ConversationAgentHarness
{
    private static bool UniqueMutationObject(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
        value.EnumerateObject().Count();

    private static bool ConfirmedNoOpWrite(JsonElement arguments, JsonElement result, ToolData data, string effectId)
    {
        if (!UniqueMutationObject(arguments) || data.Evidence?.RequestId is { } requestId && requestId != effectId ||
            !arguments.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String ||
            content.GetString() is not { } text || text.Contains('\0') ||
            !result.TryGetProperty("bytes", out var count) || !count.TryGetInt32(out var bytes) || bytes > 1024 * 1024)
            return false;
        var hash = result.GetProperty("sha256").GetString()!;
        // These are the existing bounded native writer forms. Match both physical length and
        // digest without assuming an encoding, rereading mutable files, or echoing source text.
        Encoding[] encodings = [new UTF8Encoding(false, true), new UTF8Encoding(true, true),
            new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true)];
        try
        {
            foreach (var encoding in encodings)
            {
                var preamble = encoding.GetPreamble();
                if ((long)preamble.Length + encoding.GetByteCount(text) != bytes) continue;
                var encoded = new byte[bytes];
                preamble.CopyTo(encoded, 0);
                encoding.GetBytes(text.AsSpan(), encoded.AsSpan(preamble.Length));
                if (Convert.ToHexString(SHA256.HashData(encoded)).Equals(hash, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (EncoderFallbackException) { }
        return false;
    }

    private static string AppendNoOpMutationEvidence(HarnessState state, WorkflowEffect effect, string content) =>
        content + (state.PendingName == "file.edit" ? "\nAccepted unchanged edit:\n" : "\nAccepted unchanged write:\n") +
        JsonSerializer.Serialize(new
        {
            receipt = effect.Id, tool = state.PendingName, sourceChanged = false,
            note = (state.PendingName == "file.edit"
                ? "The completed edit returned the admitted preimage file identity; it did not prove changed bytes or an applied repair. "
                : "The accepted replacement content and returned file identity equal the admitted preimage. ") +
                "This effect earned no source-generation or repair credit. Choose a material edit if a repair is still required; do not replay this completed effect."
        }, Json);
}
