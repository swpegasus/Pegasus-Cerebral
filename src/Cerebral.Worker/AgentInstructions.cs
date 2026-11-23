using System.Text;
using System.Text.Json;
using Cerebral.Core;

namespace Cerebral.Worker;

/// <summary>Revision-bound edits affect only one profile's instructions, never its execution grants.</summary>
public sealed class AgentInstructions(StateStore store, EventHub events)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public AgentInstructionDocument Get(JsonElement arguments)
    {
        ValidateFields(arguments, ["profileId"]);
        var id = RequiredText(arguments, "profileId"); Configuration.ValidateId(id);
        var profile = store.Get<AgentProfile>("agent", id) ?? throw new KeyNotFoundException("Agent profile was not found.");
        return new(profile.Id, profile.Instructions, profile.Revision);
    }

    public AgentInstructionDocument Save(JsonElement arguments)
    {
        ValidateFields(arguments, ["profileId", "content", "expectedRevision"]);
        var id = RequiredText(arguments, "profileId"); Configuration.ValidateId(id);
        var content = RequiredText(arguments, "content");
        if (!arguments.GetProperty("expectedRevision").TryGetInt32(out var expected) || expected < 0)
            throw new ArgumentException("A nonnegative current profile revision is required.");
        if (content.Contains('\0')) throw new ArgumentException("Agent instructions cannot contain NUL.");
        int bytes;
        try { bytes = Utf8.GetByteCount(content); }
        catch (EncoderFallbackException) { throw new ArgumentException("Agent instructions must contain valid Unicode."); }
        if (bytes > SystemInstructions.MaxUtf8Bytes) throw new ArgumentException("Agent instructions are limited to 32 KiB of UTF-8 text.");
        var saved = store.Locked(() =>
        {
            var profile = store.Get<AgentProfile>("agent", id) ?? throw new KeyNotFoundException("Agent profile was not found.");
            if (profile.Revision != expected)
                throw new InvalidOperationException("The agent changed since these instructions were loaded. Reload before saving; current settings were preserved.");
            profile.Instructions = content; profile.Revision = checked(profile.Revision + 1);
            store.Put("agent", profile.Id, profile);
            return new AgentInstructionDocument(profile.Id, profile.Instructions, profile.Revision);
        });
        events.Publish("agents.updated", null, new { profileId = saved.ProfileId });
        return saved;
    }

    private static string RequiredText(JsonElement arguments, string field) =>
        arguments.GetProperty(field).ValueKind == JsonValueKind.String
            ? arguments.GetProperty(field).GetString()!
            : throw new ArgumentException("Instruction fields must use their declared types.");

    private static void ValidateFields(JsonElement arguments, string[] required)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.GetRawText().Length > 256 * 1024)
            throw new ArgumentException("The instruction request exceeds its bounds.");
        var names = arguments.EnumerateObject().Select(x => x.Name).ToArray();
        if (names.Length != required.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            required.Any(x => !names.Contains(x, StringComparer.Ordinal)))
            throw new ArgumentException("The instruction request must contain exactly its declared fields.");
    }
}
