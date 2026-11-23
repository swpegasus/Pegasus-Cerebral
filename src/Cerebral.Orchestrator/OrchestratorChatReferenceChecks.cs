using System.IO;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    /// <summary>Deterministic saved-chat/parser and native control-handler fixtures; no saved owner data or model is used.</summary>
    private async Task<int> CheckChatReferencesAsync(CancellationToken token)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        const string id = "67af96c33bcd4e1b9fe04fdc8d3ad6ae";
        const string reference = "cerebral://67af96c3-3bcd-4e1b-9fe0-4fdc8d3ad6ae";
        Check(ChatReference.Create(id) == reference && ChatReference.Create(id.ToUpperInvariant()) == reference &&
            ChatReference.Create("67af96c3-3bcd-4e1b-9fe0-4fdc8d3ad6ae") == reference,
            "Saved GUID identities produce one canonical hyphenated reference.");
        Check(ChatReference.Create("legacy-native-id") is null &&
            ChatReference.NormalizeTarget("legacy-native-id") == "legacy-native-id",
            "Opaque native IDs remain exact without invented references or title matching.");
        Check(ChatReference.NormalizeTarget(reference.ToUpperInvariant()) == id,
            "A valid reference resolves to the exact canonical GuidN storage identity.");
        foreach (var malformed in new[] { "", "cerebral://" + id, reference + "/", reference + "?name=chat",
            reference + "#chat", "cerebral://chat/" + id, "cerebral://not-a-guid", "cerebral:/bad", "cerebral://user@" + id })
        {
            var rejected = false;
            try { _ = ChatReference.NormalizeTarget(malformed); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Malformed references must reject before lookup: " + malformed);
        }
        var saved = new ConversationSummary(id, "Saved name", "lead", null, "saved-run", WorkflowStatus.Completed, DateTimeOffset.UnixEpoch);
        var reads = 0;
        Task<ConversationSummary> Read(string requested, CancellationToken _)
        {
            reads++;
            Check(requested == id, "Reference lookup uses the exact saved GUID identity.");
            return Task.FromResult(saved);
        }
        Check(ReferenceEquals(await ChatReference.ReadSavedAsync(reference, Read, () => true, token), saved),
            "A matching saved read returns the actual retained chat, not a newly created one.");
        var before = reads;
        var stale = false;
        try { _ = await ChatReference.ReadSavedAsync(reference, Read, () => false, token); }
        catch (InvalidOperationException) { stale = true; }
        Check(stale && reads == before, "A stale authenticated binding rejects before the read.");
        var missing = false;
        try
        {
            _ = await ChatReference.ReadSavedAsync(reference,
                (_, _) => Task.FromException<ConversationSummary>(new KeyNotFoundException("Saved chat missing.")), () => true, token);
        }
        catch (KeyNotFoundException) { missing = true; }
        Check(missing, "An absent saved chat remains unavailable; no new-chat path is called.");
        var mismatch = false;
        try
        {
            _ = await ChatReference.ReadSavedAsync(reference,
                (_, _) => Task.FromResult(saved with { Id = "another-chat" }), () => true, token);
        }
        catch (InvalidDataException) { mismatch = true; }
        Check(mismatch, "A different returned chat cannot satisfy a reference.");
        var current = true;
        var held = new TaskCompletionSource<ConversationSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = ChatReference.ReadSavedAsync(reference, (_, _) => held.Task, () => current, token);
        current = false; held.SetResult(saved);
        stale = false;
        try { _ = await waiting; } catch (InvalidOperationException) { stale = true; }
        Check(stale, "A binding change during a saved read cannot open the old dataset's chat.");

        var original = durableConversations.GetValueOrDefault(id);
        var selected = CurrentConversationId;
        try
        {
            durableConversations[id] = saved;
            var listed = await HandleDesktopControlAsync(new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                "conversation.list", CerebralJson.Element(new { limit = 40 })), token);
            var row = listed.Data.GetProperty("conversations").EnumerateArray().Single(value => value.GetProperty("id").GetString() == id);
            Check(listed.Success && row.GetProperty("reference").GetString() == reference &&
                row.GetProperty("title").GetString() == saved.Title, "Native list exposes exact identity, human name and canonical reference.");
            var rejected = false;
            try
            {
                _ = await HandleDesktopControlAsync(new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                    "conversation.open", CerebralJson.Element(new { id = reference + "/unexpected" })), token);
            }
            catch (ArgumentException) { rejected = true; }
            Check(rejected && CurrentConversationId == selected && durableConversations.Count > 0,
                "Malformed native open leaves the current chat and retained map untouched.");
        }
        finally
        {
            if (original is null) durableConversations.Remove(id); else durableConversations[id] = original;
        }
        return checks;
    }
}
